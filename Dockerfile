# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

RUN apt update
RUN apt install -y clang zlib1g-dev

# Backend Port - CONSISTENTE
EXPOSE 5000

COPY ["rinha-backend.csproj", "."]
RUN dotnet restore "rinha-backend.csproj" --runtime linux-x64

COPY . .

WORKDIR "/src/"
RUN dotnet build "rinha-backend.csproj" -c Release -o /app/build

FROM build AS publish
# AOT corrigido - REMOVIDO UseAppHost=false
RUN dotnet publish "rinha-backend.csproj" \
-c Release \
--self-contained true \
-r linux-x64 \
-o /app/publish \
/p:PublishAot=true

RUN rm -f /app/publish/*.dbg /app/publish/*.Development.json

FROM base AS final
WORKDIR /app
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000

COPY --from=publish /app/publish .

USER root

ENTRYPOINT ["./rinha-backend"]