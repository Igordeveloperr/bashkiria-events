FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY parser/parser.csproj parser/
RUN dotnet restore parser/parser.csproj
COPY parser/ parser/
RUN dotnet publish parser/parser.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "parser.dll"]
