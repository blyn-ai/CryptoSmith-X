#!/usr/bin/env bash
# Подготовка хоста под CryptoSmith X. Ansible здесь не окупается — одна площадка,
# один оператор, — но и «руками» плохо тем, что через полгода никто не помнит, что
# именно было набрано. Это те же команды, только в гите и перезапускаемые.
#
#   ./provision-host.sh docker      Docker Engine + compose plugin
#   ./provision-host.sh postgres    PostgreSQL 16 из PGDG, данные на диске данных
#   ./provision-host.sh database    сеть докера, роль, база, пароль в .env
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

step_postgres() {
    require_root

    local pgver=16
    local datadir="${DATA_ROOT}/postgresql/${pgver}/main"

    if ! command -v psql >/dev/null 2>&1; then
        log "Ставлю PostgreSQL ${pgver} из PGDG"
        # Из PGDG, а не из Ubuntu: в дистрибутиве версия старше, а схема
        # рассчитана на 16.
        apt-get install -y -qq ca-certificates curl gnupg
        install -m 0755 -d /etc/apt/keyrings
        curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
            | gpg --dearmor --yes -o /etc/apt/keyrings/pgdg.gpg
        chmod a+r /etc/apt/keyrings/pgdg.gpg
        echo "deb [signed-by=/etc/apt/keyrings/pgdg.gpg] https://apt.postgresql.org/pub/repos/apt \
$(. /etc/os-release && echo "$VERSION_CODENAME")-pgdg main" > /etc/apt/sources.list.d/pgdg.list
        apt-get update -qq
        apt-get install -y -qq "postgresql-${pgver}"
    else
        log "PostgreSQL уже стоит: $(psql --version)"
    fi

    # Данные на отдельный диск: корень 29 ГБ, а свечи растут ~290 МБ в сутки и
    # не ротируются никогда. И весь каталог данных на ОДНОМ диске — снапшот
    # виртуалки атомарен только тогда; данные и WAL по разным томам снимались бы
    # независимо, и восстановление превратилось бы в лотерею.
    if [ ! -d "$datadir" ]; then
        log "Переношу каталог данных на ${DATA_ROOT}"
        systemctl stop "postgresql@${pgver}-main" 2>/dev/null || true
        mkdir -p "${DATA_ROOT}/postgresql/${pgver}"
        mv "/var/lib/postgresql/${pgver}/main" "${DATA_ROOT}/postgresql/${pgver}/main"
        chown -R postgres:postgres "${DATA_ROOT}/postgresql"
        chmod 700 "$datadir"
    fi

    log "Настраиваю под 26 ГБ и профиль записи"
    # Профиль нагрузки: поток мелких апсертов круглые сутки, на текущем проде
    # 175 ГБ записано против 25 ГБ прочитано. Дефолты рассчитаны на то, чтобы
    # Postgres завёлся где угодно, включая ноутбук, и для такой записи они плохи.
    #
    # max_wal_size — главный. При 1 ГБ журнал упирается в потолок постоянно, и
    # каждый раз запускается ПРИНУДИТЕЛЬНЫЙ чекпойнт. Сразу после чекпойнта
    # первая запись в страницу пишет в журнал страницу целиком, а не изменение,
    # поэтому частые чекпойнты сами себя разгоняют: больше журнала → чаще
    # чекпойнт → ещё больше журнала. 12 ГБ переводит их на таймер.
    #
    # shared_buffers — свой кеш страниц, канон 25% памяти.
    # work_mem — на одну сортировку; группировки роллапа при 4 МБ сбрасывались
    # на диск. Значение умножается на число одновременных операций, отсюда
    # умеренные 96 МБ, а не гигабайты.
    # random_page_cost — дефолтная 4 означает «диск шпиндельный, индексы дорогие»;
    # под SSD планировщик должен охотнее их брать. Гость видит диск как
    # rotational, но за ним SSD.
    # wal_compression — сжимает те самые полностраничные записи.
    cat > "/etc/postgresql/${pgver}/main/conf.d/10-csx.conf" <<CONF
data_directory = '${datadir}'

shared_buffers = 6GB
effective_cache_size = 18GB
work_mem = 96MB
maintenance_work_mem = 1GB

max_wal_size = 12GB
min_wal_size = 2GB
wal_compression = on
checkpoint_completion_target = 0.9

random_page_cost = 1.1
effective_io_concurrency = 200

max_connections = 100
CONF

    systemctl enable --now "postgresql@${pgver}-main" >/dev/null
    sleep 2
    log "Готово"
    sudo -u postgres psql -tAc "select version()" | head -1
    sudo -u postgres psql -tAc \
      "select name || ' = ' || setting || ' ' || coalesce(unit,'') from pg_settings
       where name in ('data_directory','shared_buffers','max_wal_size','work_mem',
                      'effective_cache_size','random_page_cost','wal_compression')
       order by name"
}

step_database() {
    require_root

    local pgver=16
    local net=csx
    local subnet=${CSX_SUBNET:-172.28.0.0/16}
    local gateway=${CSX_GATEWAY:-172.28.0.1}
    local envfile=/opt/cryptosmithx/.env

    # Сеть докера создаём САМИ и с фиксированной подсетью. Иначе compose выберет
    # адрес сам, и он поменяется при пересоздании — а Postgres должен слушать на
    # конкретном адресе шлюза и пускать конкретную подсеть. Заодно это избавляет
    # от listen_addresses='*': база не окажется видна в LAN клиента на
    # 192.168.0.24:5432, и firewall для этого не нужен.
    if ! docker network inspect "$net" >/dev/null 2>&1; then
        log "Создаю сеть докера $net ($subnet)"
        docker network create --subnet "$subnet" --gateway "$gateway" "$net" >/dev/null
    fi

    log "Открываю Postgres только для этой сети"
    cat > "/etc/postgresql/${pgver}/main/conf.d/20-csx-net.conf" <<CONF
# localhost — для psql на самой машине; шлюз докера — для контейнеров стека.
# Ни одного адреса, видимого из LAN клиента, здесь нет намеренно.
listen_addresses = 'localhost,${gateway}'
CONF

    local hba="/etc/postgresql/${pgver}/main/pg_hba.conf"
    if ! grep -q "csx stack" "$hba"; then
        printf '\n# csx stack: контейнеры из сети %s, только по паролю\nhost all all %s scram-sha-256\n' \
            "$net" "$subnet" >> "$hba"
    fi

    systemctl restart "postgresql@${pgver}-main"
    sleep 2

    # Пароль генерируется здесь и попадает только в .env с правами 600.
    # В историю оболочки, в аргументы команд и в вывод он не выходит.
    if [ -f "$envfile" ] && grep -q '^POSTGRES_PASSWORD=' "$envfile"; then
        log ".env уже есть, пароль не трогаю"
    else
        log "Генерирую пароль и создаю роль с базой"
        mkdir -p /opt/cryptosmithx
        local pw; pw=$(openssl rand -base64 24 | tr -d '/+=' | head -c 32)
        sudo -u postgres psql -qtAc \
            "do \$\$ begin
               if not exists (select 1 from pg_roles where rolname='marketdata') then
                 create role marketdata login;
               end if;
             end \$\$;"
        sudo -u postgres psql -qtAc "alter role marketdata password '${pw}'" >/dev/null
        sudo -u postgres psql -qtAc "select 1 from pg_database where datname='marketdata'" | grep -q 1 \
            || sudo -u postgres createdb -O marketdata marketdata
        umask 077
        cat > "$envfile" <<ENV
# Секреты этой площадки. Только здесь, никогда в git.
POSTGRES_PASSWORD=${pw}
DATABASE_CONNECTION_STRING=Host=${gateway};Port=5432;Database=marketdata;Username=marketdata;Password=${pw}
CSX_NETWORK=${net}
ENV
        chmod 600 "$envfile"
    fi

    log "Проверяю, что из контейнера видно"
    docker run --rm --network "$net" -e PGPASSWORD="$(grep '^POSTGRES_PASSWORD=' "$envfile" | cut -d= -f2-)" \
        postgres:16-alpine psql -h "$gateway" -U marketdata -d marketdata -tAc \
        "select 'из контейнера: ' || current_user || '@' || current_database()"
    echo -n "права на .env: "; stat -c %a "$envfile"
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
    docker)   step_docker ;;
    postgres) step_postgres ;;
    database) step_database ;;
    runner)   step_runner ;;
    *) echo "использование: $0 docker|postgres|runner" >&2; exit 2 ;;
esac
