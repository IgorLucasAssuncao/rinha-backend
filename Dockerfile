FROM mcr.microsoft.com/dotnet/sdk:10.0.100-preview.7-alpine3.22-aot AS build
RUN apk update \
    && apk add build-base zlib-dev
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["rinha-backend.csproj", "."]
RUN dotnet restore "rinha-backend.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "./rinha-backend.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./rinha-backend.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=true

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.0-preview.7-alpine3.22 AS final
WORKDIR /app
EXPOSE 5000
COPY --from=publish /app/publish .
ENTRYPOINT ["./rinha-backend"]
