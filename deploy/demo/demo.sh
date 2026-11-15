#!/usr/bin/env bash
# ============================================================================
# Демонстрация переключений интеграции на локальном стенде Data Platform MVP:
#   * auto  — реальная система при доступности, иначе НАШ имитатор (fallback);
#   * real  — только реальный контур (недоступность = DP-4001, без подмены);
#   * stub  — всегда наш сервис-заместитель.
# Все переключения — горячие (POST /admin/adapters/{code}/mode), без рестарта
# и без изменения кода (Strangler Fig / Adoption v1).
# Запускать после: docker compose up -d --build
# ============================================================================
set -u
DP="${DP:-http://localhost:8080}"
C1="${C1:-http://localhost:8081}"    # имитатор 1С (наш заместитель)
CB="${CB:-http://localhost:8082}"    # имитатор Битрикс24 (наш заместитель)
R1="${R1:-http://localhost:8083}"    # «реальная» 1С (docker-compose.real.yml)
RB="${RB:-http://localhost:8084}"    # «реальный» Битрикс24
H="X-Api-Key: ${DP_API_KEY:-dev-key}"
CT="Content-Type: application/json"
COMPOSE_REAL=(-f docker-compose.yml -f docker-compose.real.yml)

HAVE_DOCKER=0
if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then HAVE_DOCKER=1; fi

banner() { echo; echo "======== $* ========"; }
json() { python3 -c "import json,sys;d=json.load(sys.stdin);print(json.dumps(d,ensure_ascii=False,indent=1)[:900])" 2>/dev/null || cat; }
sync() { curl -s -H "$H" -X POST "$DP/admin/sync/$1" | json; }
mode() { curl -s -H "$H" -H "$CT" -X POST "$DP/admin/adapters/$1/mode" -d "{\"mode\":\"$2\"}" | json; }

banner "0. Здоровье стенда (db + dp-sync + rabbit)"
curl -s -H "$H" "$DP/admin/health" | json

banner "1. Режимы адаптеров (стартовые — auto, probe реальных систем)"
curl -s -H "$H" "$DP/admin/adapters" | json

banner "2. Sync 1С в режиме AUTO: real недоступна → FALLBACK на наш имитатор"
sync 1c

banner "3. Заказы в Data Platform (ext.onec.source=stub — данные из заместителя)"
curl -s -H "$H" -H "$CT" -X POST "$DP/dp/v1/stores/sales.orders/entities/Order/query" \
  -d '{"from":"Order","select":["id","number","customerName","totalAmount","ext"],"limit":3}' | json

if [[ $HAVE_DOCKER -eq 1 ]]; then
  banner "4. «Реальная» 1С появилась: поднимаем real-1c (docker-compose.real.yml)"
  docker compose "${COMPOSE_REAL[@]}" up -d --build real-1c real-bitrix >/dev/null 2>&1 || docker compose "${COMPOSE_REAL[@]}" up -d real-1c real-bitrix
  sleep 3
  sync 1c   # auto→real: endpoint real-1c, ext.onec.source=real

  banner "5. Дельта из реальной системы: rotate (+7%) и повторный sync"
  curl -s -u svc_dp:dp-secret -X POST "$R1/admin/rotate" | json
  sync 1c

  banner "6. «Реальная» система пропала: docker stop dp-real-1c → снова FALLBACK"
  docker stop dp-real-1c >/dev/null
  sync 1c   # auto→fallback: transparentно вернулись на имитатор
else
  banner "4-6. Docker недоступен — сценарий с real-контуром пропущен"
  echo "   Вручную: docker compose -f docker-compose.yml -f docker-compose.real.yml up -d"
fi

banner "7. Явный режим REAL при недоступной системе — ошибка DP-4001 (без подмены)"
mode 1c real
sync 1c

banner "8. Явный режим STUB — всегда наш имитатор, sync работает"
mode 1c stub
sync 1c

banner "9. Возврат в AUTO (производственный режим по умолчанию)"
mode 1c auto
sync 1c

banner "10. Битрикс24: sync в режиме AUTO (fallback/real) + сделки в store"
sync bitrix
curl -s -H "$H" -H "$CT" -X POST "$DP/dp/v1/stores/crm.deals/entities/Deal/query" \
  -d '{"from":"Deal","select":["id","title","stage","amount","ext"],"limit":3}' | json

banner "11. Итоговые режимы и история fallback/probe"
curl -s -H "$H" "$DP/admin/adapters" | json

banner "12. События dp.data.changed.v1 (outbox → RabbitMQ)"
curl -s -H "$H" "$DP/admin/outbox?limit=5" | json

banner "13. Журнал миграций (forward-only, применяет dp-api)"
curl -s -H "$H" "$DP/admin/migrations" | json

echo
echo "Демонстрация завершена. dp-api: $DP/swagger, dp-sync: $DP_SYNC/admin/state — http://localhost:8090/health,"
echo "RabbitMQ UI: http://localhost:15672 (guest/guest), имитаторы: $C1 и $CB"
