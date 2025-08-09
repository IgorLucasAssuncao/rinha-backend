FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
USER $APP_UID
WORKDIR /app
EXPOSE 5000

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["rinha-backend.csproj", "."]
RUN dotnet restore "rinha-backend.csproj" --runtime linux-x64
COPY . .
WORKDIR "/src"
RUN dotnet build "rinha-backend.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "rinha-backend.csproj" -c $BUILD_CONFIGURATION --runtime linux-x64 -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5000
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "rinha-backend.dll"]