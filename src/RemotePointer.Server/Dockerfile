FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props Directory.Packages.props ./
COPY src/RemotePointer.Contracts/RemotePointer.Contracts.csproj src/RemotePointer.Contracts/
COPY src/RemotePointer.Server/RemotePointer.Server.csproj src/RemotePointer.Server/
RUN dotnet restore src/RemotePointer.Server/RemotePointer.Server.csproj

COPY src/RemotePointer.Contracts/ src/RemotePointer.Contracts/
COPY src/RemotePointer.Server/ src/RemotePointer.Server/
RUN dotnet publish src/RemotePointer.Server/RemotePointer.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /output

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /output/ ./
ENV ASPNETCORE_URLS=http://+:8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "RemotePointer.Server.dll"]
