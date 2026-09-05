#!/usr/bin/env bash
# Подготовка хоста под CryptoSmith X. Ansible здесь не окупается — одна площадка,
# один оператор, — но и «руками» плохо тем, что через полгода никто не помнит, что
# именно было набрано. Это те же команды, только в гите и перезапускаемые.
#
#   ./provision-host.sh docker      Docker Engine + compose plugin
#
# Каждый шаг идемпотентен: повторный запуск ничего не ломает и не дублирует.
set -euo pipefail

DATA_ROOT=${DATA_ROOT:-/csx-data}

log() { printf '\n\033[1m== %s\033[0m\n' "$*"; }

require_root() { [ "$(id -u)" -eq 0 ] || { echo "нужен root" >&2; exit 1; }; }

step_docker() {
    require_root

    if command -v docker >/dev/null 2>&1; then
        log "Docker уже стоит: $(docker --version)"
    else
        log "Ставлю Docker из официального репозитория"
        # Репозиторий Docker, а не пакет из Ubuntu: в дистрибутиве это docker.io,
        # он отстаёт по версии и не несёт compose-плагин.
        apt-get update -qq
        apt-get install -y -qq ca-certificates curl gnupg
        install -m 0755 -d /etc/apt/keyrings
        curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
            | gpg --dearmor --yes -o /etc/apt/keyrings/docker.gpg
        chmod a+r /etc/apt/keyrings/docker.gpg
        echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
            > /etc/apt/sources.list.d/docker.list
        apt-get update -qq
        apt-get install -y -qq docker-ce docker-ce-cli containerd.io \
            docker-buildx-plugin docker-compose-plugin
    fi

    log "Настраиваю daemon.json"
    # Две вещи, обе про диск, и обе дешевле сделать до того, как что-то создано:
    #
    # data-root на ${DATA_ROOT}: корень тут 29 ГБ, а под данные отдан отдельный
    # диск. Образы, тома и логи контейнеров по умолчанию легли бы на корень и
    # рано или поздно его добили бы.
    #
    # log-opts: json-file по умолчанию растёт без предела. Контейнер, который
    # пишет в stdout круглые сутки, однажды съедает раздел — и это происходит
    # тихо, потому что место кончается не у базы, а у логов.
    mkdir -p "${DATA_ROOT}/docker"
    if [ ! -f /etc/docker/daemon.json ]; then
        mkdir -p /etc/docker
        cat > /etc/docker/daemon.json <<JSON
{
  "data-root": "${DATA_ROOT}/docker",
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" }
}
JSON
        systemctl restart docker
    else
        echo "daemon.json уже есть, не трогаю:"; cat /etc/docker/daemon.json
    fi

    systemctl enable --now docker >/dev/null
    log "Готово"
    docker --version
    docker compose version
    echo -n "data-root: "; docker info --format '{{.DockerRootDir}}'
}

case "${1:-}" in
    docker) step_docker ;;
    *) echo "использование: $0 docker" >&2; exit 2 ;;
esac
