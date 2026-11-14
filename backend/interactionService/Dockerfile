FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY interactionService/interactionService.csproj interactionService/
RUN dotnet restore interactionService/interactionService.csproj
COPY interactionService/ interactionService/
RUN dotnet publish interactionService/interactionService.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "interactionService.dll"]
