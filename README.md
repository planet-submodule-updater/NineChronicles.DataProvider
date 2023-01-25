# NineChronicles DataProvider

- NineChronicles.DataProvider is an off-chain service that stores NineChronicles game action data to a database mainly for game analysis.
- Currently, this service only supports `MySQL` database.

## Run

- Before running the program, please refer to the option values in the latest official [9c-launcher-config.json](https://release.nine-chronicles.com/9c-launcher-config.json) and fill out the variables in [appsettings.json](https://github.com/planetarium/NineChronicles.DataProvider/blob/development/NineChronicles.DataProvider.Executable/appsettings.json).

```
$ dotnet run --project ./NineChronicles.DataProvider.Executable/ -- 
```

## Development Guide

- This section lays out the steps in how to log a new action in the database.
- The [TransferAsset](https://github.com/planetarium/lib9c/blob/development/Lib9c/Action/TransferAsset.cs) action is used as an example in this guide.

### 1. Setup Database

- As a prerequisite, [MySQL](https://www.mysql.com/) and [Entity Framework Core tools](https://learn.microsoft.com/en-us/ef/core/cli/dotnet) should be installed in the local machine.
- After MySQL installation, navigate to [NineChronicles.DataProvider/NineChronicles.DataProvider.Executable](https://github.com/planetarium/NineChronicles.DataProvider/tree/development/NineChronicles.DataProvider.Executable) directory on your terminal and run the following migration command
```
dotnet ef database update -- [Connection String]
- Connection String example: "server=localhost;database=data_provider;port=3306;uid=root;pwd=root;"
```

### 2. Create Model

- In [NineChronicles.DataProvider/NineChronicles.DataProvider/Store/Models](https://github.com/planetarium/NineChronicles.DataProvider/tree/development/NineChronicles.DataProvider/Store/Models) directory, create a model file called `TransferAssetModel.cs`.
```
namespace NineChronicles.DataProvider.Store.Models
{
    using System;
    using System.ComponentModel.DataAnnotations;

    public class TransferAssetModel
    {
        [Key]
        public string? TxId { get; set; }

        public long BlockIndex { get; set; }

        public string? Sender { get; set; }

        public string? Recipient { get; set; }

        public decimal Amount { get; set; }

        public DateOnly Date { get; set; }

        public DateTimeOffset TimeStamp { get; set; }
    }
}

```

- In [NineChronicles.DataProvider/NineChronicles.DataProvider/Store/NineChroniclesContext.csNineChroniclesContext.cs](https://github.com/planetarium/NineChronicles.DataProvider/blob/development/NineChronicles.DataProvider/Store/NineChroniclesContext.cs), add a DbSet called `TransferAssets`

```
public DbSet<TransferAssetModel> TransferAssets => Set<TransferAssetModel>();
```

### 2. Create Store Method

- In [NineChronicles.DataProvider/NineChronicles.DataProvider/Store/MySqlStore.cs](https://github.com/planetarium/NineChronicles.DataProvider/blob/development/NineChronicles.DataProvider/Store/MySqlStore.cs), add a following method that stores the `TransferAsset` data into MySQL.
```
public void StoreTransferAsset(TransferAssetModel model)
{
    using NineChroniclesContext ctx = _dbContextFactory.CreateDbContext();
    TransferAssetModel? prevModel =
        ctx.TransferAssets.FirstOrDefault(r => r.TxId == model.TxId);
    if (prevModel is null)
    {
        ctx.TransferAssets.Add(model);
    }
    else
    {
        prevModel.BlockIndex = model.BlockIndex;
        prevModel.Sender = model.Sender;
        prevModel.Recipient = model.Recipient;
        prevModel.Amount = model..Amount;
        prevModel.Date = model.Date;
        prevModel.TimeStamp = model.TimeStamp;
        ctx.TransferAssets.Update(prevModel);
    }

    ctx.SaveChanges();
}

```

### 3. Render & Store Action Data

- In [NineChronicles.DataProvider/NineChronicles.DataProvider/RaiderWorker.cs](https://github.com/planetarium/NineChronicles.DataProvider/blob/development/NineChronicles.DataProvider/RaiderWorker.cs), add a following render code
```
_actionRenderer.EveryRender<TransferAsset>()
    .Subscribe(ev =>
    {
        try
        {
            if (ev.Exception is null && ev.Action is { } transferAsset)
            {
                var model = new TransferAssetModel()
                {
                    TxId = transferAsset.TxId,
                    BlockIndex = transferAsset.BlockIndex,
                    Sender = transferAsset.Sender,
                    Recipient = transferAsset.Recipient,
                    Amount = Convert.ToDecimal(transferAsset.Amount.GetQuantityString()),
                    Date = DateOnly.FromDateTime(_blockTimeOffset.DateTime),
                    TimeStamp = _blockTimeOffset,
                };
                MySqlStore.StoreTransferAsset(model);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
```

### 4. Add Database Migration

- Navigate to [NineChronicles.DataProvider/NineChronicles.DataProvider.Executable](https://github.com/planetarium/NineChronicles.DataProvider/tree/development/NineChronicles.DataProvider.Executable) directory on your terminal and run the following migration command
```
dotnet ef migrations add AddTransferAsset -- [Connection String]
- Connection String example: "server=localhost;database=data_provider;port=3306;uid=root;pwd=root;"
```
