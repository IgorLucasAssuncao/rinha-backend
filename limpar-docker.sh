#!/bin/bash
echo "🧹 Limpeza Docker Segura"

# Parar containers em execução
echo "🛑 Parando containers ativos..."
docker stop $(docker ps -q) 2>/dev/null || true

# Remover containers parados
echo "🗑️ Removendo containers parados..."
docker container prune -f

# Remover imagens não utilizadas
echo "🖼️ Removendo imagens não utilizadas..."
docker image prune -f

# NOVO: Remover TODAS as imagens
echo "🔥 Removendo TODAS as imagens..."
docker rmi $(docker images -aq) --force 2>/dev/null || true

# Remover volumes órfãos
echo "💾 Removendo volumes órfãos..."
docker volume prune -f

# Remover redes não utilizadas
echo "🌐 Removendo redes não utilizadas..."
docker network prune -f

# Limpar cache de build
echo "⚡ Limpando cache de build..."
docker builder prune -f

echo "✅ Limpeza segura concluída!"
docker system df