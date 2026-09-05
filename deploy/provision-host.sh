#!/usr/bin/env bash
# Подготовка хоста под CryptoSmith X. Ansible здесь не окупается — одна площадка,
# один оператор, — но и «руками» плохо тем, что через полгода никто не помнит, что
# именно было набрано. Это те же команды, только в гите и перезапускаемые.
#
#   ./provision-host.sh docker      Docker Engine + compose plugin
#   ./provision-host.sh runner      self-hosted GitHub Actions runner
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

step_runner() {
    require_root
    : "${RUNNER_TOKEN:?нужен RUNNER_TOKEN — короткоживущий токен регистрации}"

    local url=${RUNNER_URL:-https://github.com/blyn-ai/CryptoSmith-X}
    local version=${RUNNER_VERSION:-2.337.0}
    local labels=${RUNNER_LABELS:-csx-prod}
    local user=gh-runner
    local home="${DATA_ROOT}/gh-runner"

    # ВАЖНО, ПРО БЕЗОПАСНОСТЬ. Репозиторий публичный, а раннер стоит внутри чужой
    # сети и держит доступ к docker-сокету, то есть фактически права root.
    # На нём обязан исполняться ТОЛЬКО deploy.yml с триггером `push: [main]`:
    # запушить в main может лишь тот, у кого есть права записи. У ci.yml триггер
    # pull_request, и он должен остаться на ubuntu-latest — иначе любой желающий
    # откроет форк-PR и выполнит свой код на этой машине.
    #
    # Пользователь отдельный, не root. Он в группе docker, что на этой машине
    # равносильно root, — но пусть эта цена будет названа явно, а не размазана
    # по общей root-сессии.
    if ! id -u "$user" >/dev/null 2>&1; then
        log "Создаю пользователя $user"
        useradd --system --create-home --home-dir "$home" --shell /usr/sbin/nologin "$user"
    fi
    usermod -aG docker "$user"
    mkdir -p "$home"
    chown "$user:$user" "$home"

    if [ ! -f "$home/config.sh" ]; then
        log "Скачиваю runner $version"
        # Каталог раннера на диске данных: мусор сборок в _work весит гигабайты,
        # а корень тут 29 ГБ.
        curl -fsSL -o /tmp/runner.tar.gz \
            "https://github.com/actions/runner/releases/download/v${version}/actions-runner-linux-x64-${version}.tar.gz"
        tar xzf /tmp/runner.tar.gz -C "$home"
        rm -f /tmp/runner.tar.gz
        chown -R "$user:$user" "$home"
    fi

    if [ -f "$home/.runner" ]; then
        log "Раннер уже зарегистрирован, пропускаю регистрацию"
    else
        log "Регистрирую раннер"
        sudo -u "$user" "$home/config.sh" --unattended --replace \
            --url "$url" --token "$RUNNER_TOKEN" \
            --name "$(hostname)" --labels "$labels" --work _work
    fi

    log "Ставлю как службу"
    ( cd "$home" && ./svc.sh install "$user" >/dev/null 2>&1 || true; ./svc.sh start >/dev/null 2>&1 || true )
    sleep 3
    ( cd "$home" && ./svc.sh status 2>&1 | head -4 )
}

case "${1:-}" in
    docker) step_docker ;;
    runner) step_runner ;;
    *) echo "использование: $0 docker|runner" >&2; exit 2 ;;
esac
