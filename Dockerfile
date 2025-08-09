# Etapa de Build - SDK completo para compila��o AOT
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copia apenas .csproj para otimizar cache de depend�ncias
COPY *.csproj ./
RUN dotnet restore --runtime linux-x64

# Copia todo o c�digo fonte
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

# Etapa de Runtime - Apenas depend�ncias essenciais 
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-alpine AS runtime
WORKDIR /app
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000

# Copia APENAS o bin�rio nativo e depend�ncias necess�rias
COPY --from=build /app/out ./

# Tornar execut�vel (necess�rio no Alpine)
RUN chmod +x ./rinha-backend

# Executar o bin�rio nativo diretamente
ENTRYPOINT ["./rinha-backend"]