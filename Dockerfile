# Etapa de Build - SDK completo para compilação AOT
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copia apenas .csproj para otimizar cache de dependências
COPY *.csproj ./
RUN dotnet restore --runtime linux-x64

# Copia todo o código fonte
COPY . .

# Publica com Native AOT + Trimming agressivo
RUN dotnet publish \
  -c Release \
  -r linux-x64 \
  -o /app/out \
  --self-contained true \
  --no-restore \
  /p:PublishAot=true \
  /p:PublishTrimmed=true \
  /p:TrimMode=Link \
  /p:PublishSingleFile=false

# Debug: Verificar o que foi gerado
RUN ls -la /app/out/

# Etapa de Runtime - Apenas dependências essenciais 
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-alpine AS runtime
WORKDIR /app
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000

# Copia APENAS o binário nativo e dependências necessárias
COPY --from=build /app/out ./

# Tornar executável (necessário no Alpine)
RUN chmod +x ./rinha-backend

# Executar o binário nativo diretamente
ENTRYPOINT ["./rinha-backend"]