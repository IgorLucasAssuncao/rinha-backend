#!/bin/bash
echo "🧹 Limpeza Docker COMPLETA (Preservando Imagens Microsoft)"

# Parar ALL containers
echo "🛑 Parando TODOS os containers..."
docker stop $(docker ps -aq) 2>/dev/null || true

# Remover ALL containers
echo "🗑️ Removendo TODOS os containers..."
docker rm -f $(docker ps -aq) 2>/dev/null || true

# Limpar containers órfãos
echo "🧽 Limpando containers órfãos..."
docker container prune -f

# ✅ Remover imagens (preservando Microsoft)
echo "🔥 Removendo TODAS as imagens (preservando Microsoft)..."
docker images | grep -v "mcr.microsoft.com" | grep -v "REPOSITORY" | awk '{print $3}' | sort -u | xargs -r docker rmi --force 2>/dev/null || true

# Limpar imagens dangling
echo "👻 Removendo imagens dangling..."
docker image prune -f

# Remover ALL volumes
echo "💾 Removendo TODOS os volumes..."
docker volume rm $(docker volume ls -q) 2>/dev/null || true
docker volume prune -f

# Remover ALL networks (exceto padrão)
echo "🌐 Removendo TODAS as redes customizadas..."
docker network rm $(docker network ls -q --filter type=custom) 2>/dev/null || true
docker network prune -f

# ⚡ LIMPEZA COMPLETA DE CACHE
echo "⚡ Removendo TODO o cache de build..."
docker builder prune -a -f

# Limpar buildx cache (se disponível)
echo "🔥 Limpando cache buildx..."
docker buildx prune -a -f 2>/dev/null || true

# Limpar system cache COMPLETO
echo "🧨 Limpeza system AGRESSIVA..."
docker system prune -a -f --volumes

# Remover buildkit cache manual
echo "🗄️ Removendo buildkit cache manual..."
rm -rf ~/.docker/buildx/instances/*/buildkitd.toml.lock 2>/dev/null || true
rm -rf /var/lib/docker/buildkit/* 2>/dev/null || true

# Restart Docker daemon (se possível)
echo "🔄 Tentando restart do Docker daemon..."
sudo systemctl restart docker 2>/dev/null || true
sudo service docker restart 2>/dev/null || true

# Esperar daemon subir
echo "⏳ Aguardando Docker daemon..."
sleep 3

# Verificar se Docker está funcionando
docker version >/dev/null 2>&1 && echo "✅ Docker funcionando" || echo "❌ Docker com problemas"

# ⚡ LIMPEZA FINAL - Cache do host
echo "🧼 Limpeza final de caches..."
sync && echo 3 | sudo tee /proc/sys/vm/drop_caches >/dev/null 2>&1 || true

# Mostrar resultado
echo ""
echo "📋 Imagens Microsoft preservadas:"
docker images | grep "mcr.microsoft.com" 2>/dev/null || echo "Nenhuma imagem Microsoft encontrada"

echo ""
echo "📊 Status final do Docker:"
docker system df

echo ""
echo "🎯 Containers ativos:"
docker ps

echo ""
echo "💿 Volumes restantes:"
docker volume ls

echo ""
echo "🌐 Redes restantes:"
docker network ls

echo ""
echo "✅ Limpeza ULTRA COMPLETA concluída!"
echo "💡 Dica: Restart sua máquina para limpeza completa da memória"