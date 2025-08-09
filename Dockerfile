FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["rinha-backend.csproj", "."]
RUN dotnet restore "./rinha-backend.csproj" --runtime linux-x64

COPY . .
RUN dotnet publish "./rinha-backend.csproj" \
  -c Release \
  -o /app/publish \
  --runtime linux-x64 \
  --self-contained true \
  --no-restore \
  /p:PublishAot=true \
  /p:PublishTrimmed=true \
  /p:PublishSingleFile=false

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0 AS final
WORKDIR /app
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000

COPY --from=build /app/publish .

ENTRYPOINT ["./rinha-backend"]