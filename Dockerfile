FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DbAccessManager.csproj ./
RUN dotnet restore DbAccessManager.csproj
COPY . ./
RUN dotnet publish DbAccessManager.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
USER root
RUN mkdir -p /var/query-gate/keys && chown -R $APP_UID:$APP_UID /var/query-gate
COPY --from=build /app/publish ./
USER $APP_UID
EXPOSE 5176
ENTRYPOINT ["dotnet", "DbAccessManager.dll"]
