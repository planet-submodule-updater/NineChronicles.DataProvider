namespace NineChronicles.DataProvider.Tools.SubCommand
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Cocona;
    using Libplanet;
    using Libplanet.Action;
    using Libplanet.Blockchain;
    using Libplanet.Blockchain.Policies;
    using Libplanet.Blocks;
    using Libplanet.RocksDBStore;
    using Libplanet.Store;
    using MySqlConnector;
    using Nekoyume.Action;
    using Nekoyume.BlockChain;
    using Serilog;
    using Serilog.Events;
    using NCAction = Libplanet.Action.PolymorphicAction<Nekoyume.Action.ActionBase>;

    public class MySqlMigration
    {
        private const string AgentDbName = "Agents";
        private const string AvatarDbName = "Avatars";
        private const string HasDbName = "HackAndSlashes";
        private string _connectionString;
        private IStore _baseStore;
        private BlockChain<NCAction> _baseChain;
        private StreamWriter _agentBulkFile;
        private StreamWriter _avatarBulkFile;
        private StreamWriter _hasBulkFile;
        private List<string> _agentList;
        private List<string> _avatarList;
        private List<string> _agentFiles;
        private List<string> _avatarFiles;
        private List<string> _hasFiles;

        [Command(Description = "Migrate action data in rocksdb store to mysql db.")]
        public void Migration(
            [Option('o', Description = "Rocksdb path to migrate.")]
            string storePath,
            [Option(
                "rocksdb-storetype",
                Description = "Store type of RocksDb (new or mono).")]
            string rocksdbStoreType,
            [Option(
                "mysql-server",
                Description = "A hostname of MySQL server.")]
            string mysqlServer,
            [Option(
                "mysql-port",
                Description = "A port of MySQL server.")]
            uint mysqlPort,
            [Option(
                "mysql-username",
                Description = "The name of MySQL user.")]
            string mysqlUsername,
            [Option(
                "mysql-password",
                Description = "The password of MySQL user.")]
            string mysqlPassword,
            [Option(
                "mysql-database",
                Description = "The name of MySQL database to use.")]
            string mysqlDatabase,
            [Option(
                "offset",
                Description = "offset of block index (no entry will migrate from the genesis block).")]
            int? offset = null,
            [Option(
                "limit",
                Description = "limit of block count (no entry will migrate to the chain tip).")]
            int? limit = null
        )
        {
            DateTimeOffset start = DateTimeOffset.UtcNow;
            var builder = new MySqlConnectionStringBuilder
            {
                Database = mysqlDatabase,
                UserID = mysqlUsername,
                Password = mysqlPassword,
                Server = mysqlServer,
                Port = mysqlPort,
                AllowLoadLocalInfile = true,
            };

            _connectionString = builder.ConnectionString;

            Console.WriteLine("Setting up RocksDBStore...");
            if (rocksdbStoreType == "new")
            {
                _baseStore = new RocksDBStore(
                    storePath,
                    dbConnectionCacheSize: 10000);
            }
            else if (rocksdbStoreType == "mono")
            {
                _baseStore = new MonoRocksDBStore(storePath);
            }
            else
            {
                throw new CommandExitedException("Invalid rocksdb-storetype. Please enter 'new' or 'mono'", -1);
            }

            long totalLength = _baseStore.CountBlocks();

            if (totalLength == 0)
            {
                throw new CommandExitedException("Invalid rocksdb-store. Please enter a valid store path", -1);
            }

            if (!(_baseStore.GetCanonicalChainId() is Guid chainId))
            {
                Console.Error.WriteLine("There is no canonical chain: {0}", storePath);
                Environment.Exit(1);
                return;
            }

            if (!(_baseStore.IndexBlockHash(chainId, 0) is { } gHash))
            {
                Console.Error.WriteLine("There is no genesis block: {0}", storePath);
                Environment.Exit(1);
                return;
            }

            // Setup base store
            RocksDBKeyValueStore baseStateRootKeyValueStore = new RocksDBKeyValueStore(Path.Combine(storePath, "state_hashes"));
            RocksDBKeyValueStore baseStateKeyValueStore = new RocksDBKeyValueStore(Path.Combine(storePath, "states"));
            TrieStateStore baseStateStore =
                new TrieStateStore(baseStateKeyValueStore, baseStateRootKeyValueStore);

            // Setup block policy
            const int minimumDifficulty = 5000000, maximumTransactions = 100;
            IStagePolicy<NCAction> stagePolicy = new VolatileStagePolicy<NCAction>();
            LogEventLevel logLevel = LogEventLevel.Debug;
            var blockPolicySource = new BlockPolicySource(Log.Logger, logLevel);
            IBlockPolicy<NCAction> blockPolicy = blockPolicySource.GetPolicy(minimumDifficulty, maximumTransactions);

            // Setup base chain & new chain
            Block<NCAction> genesis = _baseStore.GetBlock<NCAction>(gHash);
            _baseChain = new BlockChain<NCAction>(blockPolicy, stagePolicy, _baseStore, baseStateStore, genesis);

            // Prepare block hashes to append to new chain
            long height = _baseChain.Tip.Index;
            if (offset + limit > (int)height)
            {
                Console.Error.WriteLine(
                    "The sum of the offset and limit is greater than the chain tip index: {0}",
                    height);
                Environment.Exit(1);
                return;
            }

            Console.WriteLine("Start migration.");

            // files to store bulk file paths (new file created every 10000 blocks for bulk load performance)
            _agentFiles = new List<string>();
            _avatarFiles = new List<string>();
            _hasFiles = new List<string>();

            // lists to keep track of inserted addresses to minimize duplicates
            _agentList = new List<string>();
            _avatarList = new List<string>();

            CreateBulkFiles();
            int totalCount = limit ?? (int)_baseStore.CountBlocks();
            Console.WriteLine("Migrating data from block #{0} to #{1}", offset ?? 0, offset ?? 0 + totalCount - 1);
            Task<bool>[] taskArray = new Task<bool>[totalCount];

            try
            {
                foreach (var item in
                    _baseStore.IterateIndexes(_baseChain.Id, offset ?? 0, limit).Select((value, i) => new { i, value }))
                {
                    if (item.i > 0 && item.i % 5000 == 0 && item.i + 1 != totalCount)
                    {
                        Thread.Sleep(1500);
                        FlushBulkFiles();
                        CreateBulkFiles();
                        Thread.Sleep(1500);
                    }

                    Console.WriteLine($"Block progress: {item.i}/{totalCount}");
                    var block = _baseStore.GetBlock<NCAction>(item.value);
                    taskArray[item.i] = Task.Factory.StartNew(() =>
                    {
                        List<ActionEvaluation> actionEvaluations = EvaluateBlock(block);
                        ProcessActionEvaluation(actionEvaluations);
                        return true;
                    });
                }

                Task.WaitAll(taskArray);
                Thread.Sleep(1500);
                FlushBulkFiles();
                DateTimeOffset postDataPrep = DateTimeOffset.Now;
                Console.WriteLine("Data Preparation Complete! Time Elapsed: {0}", postDataPrep - start);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }


            foreach (var path in _agentFiles)
            {
                BulkInsert(AgentDbName, path);
            }

            foreach (var path in _avatarFiles)
            {
                BulkInsert(AvatarDbName, path);
            }

            foreach (var path in _hasFiles)
            {
                BulkInsert(HasDbName, path);
            }

            DateTimeOffset end = DateTimeOffset.UtcNow;
            Console.WriteLine("Migration from block #{0} to #{1} Complete! Time Elapsed: {2}", offset ?? 0, offset ?? 0 + totalCount - 1, end - start);
        }

        private void ProcessActionEvaluation(List<ActionEvaluation> actionEvaluations)
        {
            foreach (var ae in actionEvaluations)
            {
                if (ae.Action is PolymorphicAction<ActionBase> action)
                {
                    // avatarNames will be stored as "N/A" for optimization
                    if (action.InnerAction is HackAndSlash2 hasAction2)
                    {
                        Address signer = ae.InputContext.Signer;
                        WriteHackAndSlash(
                            hasAction2.Id,
                            ae.InputContext.BlockIndex,
                            signer,
                            hasAction2.avatarAddress,
                            "N/A",
                            hasAction2.stageId,
                            hasAction2.Result is { IsClear: true });
                    }

                    if (ae.Action is HackAndSlash3 hasAction3)
                    {
                        Address signer = ae.InputContext.Signer;
                        WriteHackAndSlash(
                            hasAction3.Id,
                            ae.InputContext.BlockIndex,
                            signer,
                            hasAction3.avatarAddress,
                            "N/A",
                            hasAction3.stageId,
                            hasAction3.Result is { IsClear: true });
                    }

                    if (ae.Action is HackAndSlash4 hasAction4)
                    {
                        Address signer = ae.InputContext.Signer;
                        WriteHackAndSlash(
                            hasAction4.Id,
                            ae.InputContext.BlockIndex,
                            signer,
                            hasAction4.avatarAddress,
                            "N/A",
                            hasAction4.stageId,
                            hasAction4.Result is { IsClear: true });
                    }
                }
            }
        }

        private List<ActionEvaluation> EvaluateBlock(Block<NCAction> block)
        {
            var evList = block.Evaluate(
                DateTimeOffset.Now,
                address => _baseChain.GetState(address, block.Hash),
                (address, currency) =>
                    _baseChain.GetBalance(address, currency, block.Hash)).ToList();
            return evList;
        }

        private void FlushBulkFiles()
        {
            _agentBulkFile.Flush();
            _agentBulkFile.Close();

            _avatarBulkFile.Flush();
            _avatarBulkFile.Close();

            _hasBulkFile.Flush();
            _hasBulkFile.Close();
        }

        private void CreateBulkFiles()
        {
            string agentFilePath = Path.GetTempFileName();
            _agentBulkFile = new StreamWriter(agentFilePath);

            string avatarFilePath = Path.GetTempFileName();
            _avatarBulkFile = new StreamWriter(avatarFilePath);

            string hasFilePath = Path.GetTempFileName();
            _hasBulkFile = new StreamWriter(hasFilePath);

            _agentFiles.Add(agentFilePath);
            _avatarFiles.Add(avatarFilePath);
            _hasFiles.Add(hasFilePath);
        }

        private void BulkInsert(
            string tableName,
            string filePath)
        {
            using MySqlConnection connection = new MySqlConnection(_connectionString);
            try
            {
                DateTimeOffset start = DateTimeOffset.Now;
                Console.WriteLine($"Start bulk insert to {tableName}.");
                MySqlBulkLoader loader = new MySqlBulkLoader(connection)
                {
                    TableName = tableName,
                    FileName = filePath,
                    Timeout = 0,
                    LineTerminator = "\n",
                    FieldTerminator = ";",
                    Local = true,
                };

                loader.Load();
                Console.WriteLine($"Bulk load to {tableName} complete.");
                DateTimeOffset end = DateTimeOffset.Now;
                Console.WriteLine("Time elapsed: {0}", end - start);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                Console.WriteLine($"Bulk load to {tableName} failed.");
            }
        }

        private void WriteHackAndSlash(
            Guid actionId,
            long blockIndex,
            Address agentAddress,
            Address avatarAddress,
            string avatarName,
            int stageId,
            bool isClear)
        {
            try
            {
                // check if address is already in _agentList
                if (!_agentList.Contains(agentAddress.ToString()))
                {
                    _agentBulkFile.WriteLine(
                        $"{agentAddress.ToString()};");
                    _agentList.Add(agentAddress.ToString());
                }

                // check if address is already in _avatarList
                if (!_avatarList.Contains(avatarAddress.ToString()))
                {
                    _avatarBulkFile.WriteLine(
                        $"{avatarAddress.ToString()};" +
                        $"{agentAddress.ToString()};" +
                        $"{avatarName ?? "N/A"}");
                    _avatarList.Add(avatarAddress.ToString());
                }

                _hasBulkFile.WriteLine(
                    $"{actionId.ToString()};" +
                    $"{avatarAddress.ToString()};" +
                    $"{agentAddress.ToString()};" +
                    $"{stageId};" +
                    $"{isClear};" +
                    $"{stageId > 10000000};" +
                    $"{blockIndex.ToString()}");
            }
            catch (Exception e)
            {
                Console.Write(e.Message);
            }
        }
    }
}
