#!/usr/bin/env bash
# Сквозной smoke-тест Dp.Platform против запущенного docker compose.
# Предусловие: docker compose up -d --build; шлюз доступен на localhost:8080.
set -u
GW="${DP_GATEWAY:-http://localhost:8080}"
TOKEN="Authorization: Bearer ${DP_API_TOKEN:-dp-dev-token}"
PASS=0; FAIL=0

run() {
  if [ "$2" = "$3" ]; then PASS=$((PASS+1)); echo "  [OK]   $1"
  else FAIL=$((FAIL+1)); echo "  [FAIL] $1 (ожидался HTTP $2, получен $3)"; fi
}

echo "== 1. Карта сервисов и health =="
code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 "$GW/health/services")
run "агрегированный /health/services" 200 "$code"

echo "== 2. Реестр схем (SD DSL v1) =="
code=$(curl -s -o /tmp/reg1.json -w "%{http_code}" -m 5 -X POST "$GW/api/v1/stores" -H "$TOKEN" -H "Content-Type: application/json" -d '{
  "store":"smoke.items","version":"1.0.0","primaryKey":"item_id",
  "fields":[{"name":"item_id","type":"uuid","required":true},{"name":"title","type":"string"},{"name":"qty","type":"int"}]
}')
run "регистрация smoke.items@1.0.0" 201 "$code"
code=$(curl -s -o /tmp/reg2.json -w "%{http_code}" -m 5 -X POST "$GW/api/v1/stores" -H "$TOKEN" -H "Content-Type: application/json" -d '{
  "store":"smoke.items","version":"1.0.1","primaryKey":"item_id",
  "fields":[{"name":"item_id","type":"uuid","required":true},{"name":"qty","type":"long"}]
}')
run "ломающее изменение отклонено (DP-1004)" 409 "$code"

echo "== 3. Data Access API =="
K=99999999-1111-2222-3333-444444444444
code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 -X POST "$GW/api/v1/data/smoke.items" -H "$TOKEN" -H "Content-Type: application/json" -d "{\"item_id\":\"$K\",\"title\":\"alpha\",\"qty\":5}")
run "insert" 201 "$code"
code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 "$GW/api/v1/data/smoke.items/$K" -H "$TOKEN")
run "get" 200 "$code"
code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 -X PATCH "$GW/api/v1/data/smoke.items/$K" -H "$TOKEN" -H "Content-Type: application/json" -d '{"qty":7}')
run "patch" 200 "$code"
code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 -X DELETE "$GW/api/v1/data/smoke.items/$K" -H "$TOKEN")
run "delete" 204 "$code"

echo "== 4. Файлы =="
echo "smoke $(date +%s)" > /tmp/smoke-upload.txt
code=$(curl -s -o /tmp/smoke-up.json -w "%{http_code}" -m 5 -X POST "$GW/api/v1/files" -H "$TOKEN" -F "file=@/tmp/smoke-upload.txt")
run "upload" 201 "$code"
FID=$(python3 -c "import json;print(json.load(open('/tmp/smoke-up.json'))['id'])" 2>/dev/null || echo "")
code=$(curl -s -o /tmp/smoke-dl.txt -w "%{http_code}" -m 5 "$GW/api/v1/files/$FID" -H "$TOKEN")
run "download" 200 "$code"

echo "== 5. Замещение 1С/Битрикс =="
active=$(curl -s -m 5 -H "$TOKEN" "$GW/api/v1/int/1c/route" | python3 -c "import sys,json;print(json.load(sys.stdin).get('active'))" 2>/dev/null)
run "1С замещена имитатором (active=stub)" 1 "$([ "$active" = "stub" ] && echo 1 || echo 0)"
code=$(curl -s -o /dev/null -w "%{http_code}" -m 10 "$GW/api/v1/int/1c/documents" -H "$TOKEN")
run "documents 1С через замещение" 200 "$code"
code=$(curl -s -o /dev/null -w "%{http_code}" -m 10 "$GW/api/v1/int/bitrix/documents" -H "$TOKEN")
run "documents Битрикс через замещение" 200 "$code"

echo
echo "ИТОГО: PASS=$PASS FAIL=$FAIL"
[ "$FAIL" = "0" ] && echo "SMOKE-ТЕСТ ПРОЙДЕН" || echo "ЕСТЬ ОШИБКИ"
exit $([ "$FAIL" = "0" ] && echo 0 || echo 1)
