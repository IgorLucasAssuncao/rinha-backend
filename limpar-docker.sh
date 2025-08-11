#!/bin/bash
echo "🧹 Limpeza Docker Seletiva (Preservando Imagens Microsoft)"

# Parar containers
echo "🛑 Parando containers ativos..."
docker stop $(docker ps -q) 2>/dev/null || true

# Limpar containers
echo "🗑️ Removendo containers parados..."
docker container prune -f

# ✅ SOLUÇÃO SIMPLES: Remover por filtro negativo
echo "🔥 Removendo imagens (preservando Microsoft)..."
docker images | grep -v "mcr.microsoft.com" | grep -v "REPOSITORY" | awk '{print $3}' | sort -u | xargs -r docker rmi --force 2>/dev/null || true

# Limpar resto
echo "💾 Removendo volumes órfãos..."
docker volume prune -f

echo "🌐 Removendo redes não utilizadas..."
docker network prune -f

echo "⚡ Limpando cache de build..."
docker builder prune -f

# Mostrar resultado
echo ""
echo "📋 Imagens Microsoft preservadas:"
docker images | grep "mcr.microsoft.com"

echo ""
echo "✅ Limpeza seletiva concluída!"
docker system df