FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build-env
WORKDIR /app
ARG COMMIT

# Copy csproj and restore as distinct layers
COPY ./NineChronicles.Headless/Lib9c/Lib9c/Lib9c.csproj ./Lib9c/
COPY ./NineChronicles.Headless/Libplanet.Headless/Libplanet.Headless.csproj ./Libplanet.Headless/
COPY ./NineChronicles.Headless/NineChronicles.RPC.Shared/NineChronicles.RPC.Shared/NineChronicles.RPC.Shared.csproj ./NineChronicles.RPC.Shared/
COPY ./NineChronicles.Headless/NineChronicles.Headless/NineChronicles.Headless.csproj ./NineChronicles.Headless/
COPY ./NineChronicles.Headless/NineChronicles.Headless.Executable/NineChronicles.Headless.Executable.csproj ./NineChronicles.Headless.Executable/
COPY ./NineChronicles.DataProvider/NineChronicles.DataProvider.csproj ./NineChronicles.DataProvider/
COPY ./NineChronicles.DataProvider.Executable/NineChronicles.DataProvider.Executable.csproj ./NineChronicles.DataProvider.Executable/
RUN dotnet restore Lib9c
RUN dotnet restore Libplanet.Headless
RUN dotnet restore NineChronicles.RPC.Shared
RUN dotnet restore NineChronicles.Headless
RUN dotnet restore NineChronicles.Headless.Executable -r linux-x64
RUN dotnet restore NineChronicles.DataProvider
RUN dotnet restore NineChronicles.DataProvider.Executable

# Copy everything else and build
COPY . ./
RUN dotnet publish NineChronicles.DataProvider.Executable/NineChronicles.DataProvider.Executable.csproj \
    -c Release \
    -r linux-x64 \
    -o out \
    --self-contained \
    --version-suffix $COMMIT
 
RUN dotnet publish NineChronicles.Headless.Executable/NineChronicles.Headless.Executable.csproj \
    -c Release \
    -r linux-x64 \
    -o out \
    --self-contained \
    --version-suffix $COMMIT

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:6.0
WORKDIR /app
RUN apt-get update && apt-get install -y libc6-dev jq
COPY --from=build-env /app/out .

RUN apt-get update \
    && apt-get install -y --allow-unauthenticated \
        libc6-dev jq curl \
     && rm -rf /var/lib/apt/lists/*

VOLUME /data

ENTRYPOINT ["dotnet", "NineChronicles.DataProvider.Executable.dll"]
