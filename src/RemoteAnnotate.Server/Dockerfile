FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props Directory.Packages.props version.json ./
COPY src/RemoteAnnotate.Contracts/RemoteAnnotate.Contracts.csproj src/RemoteAnnotate.Contracts/
COPY src/RemoteAnnotate.Server/RemoteAnnotate.Server.csproj src/RemoteAnnotate.Server/
RUN dotnet restore src/RemoteAnnotate.Server/RemoteAnnotate.Server.csproj

COPY src/RemoteAnnotate.Contracts/ src/RemoteAnnotate.Contracts/
COPY src/RemoteAnnotate.Server/ src/RemoteAnnotate.Server/
COPY .git/ .git/
RUN dotnet publish src/RemoteAnnotate.Server/RemoteAnnotate.Server.csproj \
    --configuration Release \
    --no-restore \
    -p:PublicRelease=true \
    --output /output

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /output/ ./
ENV ASPNETCORE_URLS=http://+:8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "RemoteAnnotate.Server.dll"]
