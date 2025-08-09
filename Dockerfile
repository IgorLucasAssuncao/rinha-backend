FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

COPY *.csproj ./
RUN dotnet restore --runtime linux-x64

COPY . .
RUN dotnet publish \
-c Release \
-r linux-x64 \
-o /app/out \
--self-contained true \
--no-restore \
/p:PublishAot=true \
/p:PublishTrimmed=true

# Debug: Ver o que foi gerado
RUN ls -la /app/out/ && find /app/out -name "*backend*"

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0 AS runtime
WORKDIR /app
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000

COPY --from=build /app/out ./

# Tornar executável e criar script inteligente
RUN if [ -f "./rinha-backend" ]; then \
    chmod +x ./rinha-backend; \
    echo "AOT executável encontrado"; \
    echo "#!/bin/bash\nexec ./rinha-backend \"\$@\"" > /app/start.sh; \
else \
    echo "AOT falhou, usando DLL"; \
    echo "#!/bin/bash\nexec dotnet rinha-backend.dll \"\$@\"" > /app/start.sh; \
fi && \
chmod +x /app/start.sh && \
ls -la

ENTRYPOINT ["/app/start.sh"]