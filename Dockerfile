FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 5000

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["rinha-backend.csproj", "."]
RUN dotnet restore "./rinha-backend.csproj" --runtime linux-x64

COPY . .
RUN dotnet publish "./rinha-backend.csproj" \
-c Release \
-o /app/publish \
--runtime linux-x64 \
--self-contained false \
--no-restore \
/p:PublishAot=true \ 
/p:PublishTrimmed=true \
/p:PublishSingleFile=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID
ENTRYPOINT ["dotnet", "rinha-backend.dll"]