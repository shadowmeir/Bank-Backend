#!/usr/bin/env bash
set -euo pipefail

BASE="http://localhost:5080"   # <-- set this to EXACTLY what Kestrel prints
PWA="P@ssw0rd!123"
PWB="P@ssw0rd!123"

require() { command -v "$1" >/dev/null || { echo "Missing dependency: $1"; exit 1; }; }
require curl
require jq
require uuidgen

post_json() {
  local url="$1"
  local json="$2"
  local token="${3:-}"
  local idem="${4:-}"

  # curl prints body, then a newline, then HTTP code on the last line
  local out
  if [[ -n "$token" && -n "$idem" ]]; then
    out=$(curl -sS -k -X POST "$BASE$url" \
      -H "Authorization: Bearer $token" \
      -H "Idempotency-Key: $idem" \
      -H "Content-Type: application/json" \
      -d "$json" \
      -w "\n%{http_code}")
  elif [[ -n "$token" ]]; then
    out=$(curl -sS -k -X POST "$BASE$url" \
      -H "Authorization: Bearer $token" \
      -H "Content-Type: application/json" \
      -d "$json" \
      -w "\n%{http_code}")
  else
    out=$(curl -sS -k -X POST "$BASE$url" \
      -H "Content-Type: application/json" \
      -d "$json" \
      -w "\n%{http_code}")
  fi

  local code body
  code="$(echo "$out" | tail -n 1)"
  body="$(echo "$out" | sed '$d')"

  if [[ "$code" != 2* ]]; then
    echo "POST $url failed: HTTP $code"
    echo "Response body:"
    echo "$body" | jq . || echo "$body"
    exit 1
  fi

  echo "$body"
}

get_json() {
  local url="$1"
  local token="$2"

  local out code body
  out=$(curl -sS -k -X GET "$BASE$url" \
    -H "Authorization: Bearer $token" \
    -w "\n%{http_code}")

  code="$(echo "$out" | tail -n 1)"
  body="$(echo "$out" | sed '$d')"

  if [[ "$code" != 2* ]]; then
    echo "GET $url failed: HTTP $code"
    echo "Response body:"
    echo "$body" | jq . || echo "$body"
    exit 1
  fi

  echo "$body"
}

echo "== Register user A =="
EMAIL_A="a$(date +%s)@test.com"
RES_A=$(post_json "/auth/register" "{\"email\":\"$EMAIL_A\",\"password\":\"$PWA\"}")
TOK_A=$(echo "$RES_A" | jq -r '.accessToken')
[[ "$TOK_A" != "null" && -n "$TOK_A" ]] || { echo "Register A did not return accessToken"; echo "$RES_A" | jq .; exit 1; }

echo "== Register user B =="
EMAIL_B="b$(date +%s)@test.com"
RES_B=$(post_json "/auth/register" "{\"email\":\"$EMAIL_B\",\"password\":\"$PWB\"}")
TOK_B=$(echo "$RES_B" | jq -r '.accessToken')
[[ "$TOK_B" != "null" && -n "$TOK_B" ]] || { echo "Register B did not return accessToken"; echo "$RES_B" | jq .; exit 1; }

echo "== Create account for A (ILS) =="
ACC_A=$(post_json "/accounts" '{"currency":"ILS"}' "$TOK_A" | jq -r '.accountId')

echo "== Create account for B (ILS) =="
ACC_B=$(post_json "/accounts" '{"currency":"ILS"}' "$TOK_B" | jq -r '.accountId')

echo "Account A: $ACC_A"
echo "Account B: $ACC_B"

echo "== Deposit 1000 to A =="
DEP_KEY=$(uuidgen)
post_json "/accounts/$ACC_A/deposit" '{"amount":1000,"description":"seed"}' "$TOK_A" "$DEP_KEY" | jq .

echo "== Transfer 250 from A -> B =="
TR_KEY=$(uuidgen)
TR_RES=$(post_json "/transactions/transfer" \
  "{\"fromAccountId\":\"$ACC_A\",\"toAccountId\":\"$ACC_B\",\"amount\":250,\"description\":\"rent\"}" \
  "$TOK_A" "$TR_KEY")

echo "$TR_RES" | jq .
CORR=$(echo "$TR_RES" | jq -r .correlationId)
echo "CorrelationId: $CORR"

echo "== List transactions for A =="
TX_A=$(get_json "/transactions?accountId=$ACC_A&limit=50" "$TOK_A")
echo "$TX_A" | jq .

echo "== List transactions for B =="
TX_B=$(get_json "/transactions?accountId=$ACC_B&limit=50" "$TOK_B")
echo "$TX_B" | jq .

echo "== Assertions (enums as strings) =="
echo "$TX_A" | jq -e --arg corr "$CORR" --arg accb "$ACC_B" '
  .items | any(.type=="TransferOut" and .correlationId==$corr and .counterpartyAccountId==$accb and .amount==-250)
' >/dev/null && echo "A has TransferOut (-250) with correct corr + counterparty ✅"

echo "$TX_B" | jq -e --arg corr "$CORR" --arg acca "$ACC_A" '
  .items | any(.type=="TransferIn" and .correlationId==$corr and .counterpartyAccountId==$acca and .amount==250)
' >/dev/null && echo "B has TransferIn (+250) with correct corr + counterparty ✅"

echo "== Repeat same transfer with same Idempotency-Key (should 409) =="
out=$(curl -sS -k -o /tmp/dup.json -w "%{http_code}" -X POST "$BASE/transactions/transfer" \
  -H "Authorization: Bearer $TOK_A" \
  -H "Idempotency-Key: $TR_KEY" \
  -H "Content-Type: application/json" \
  -d "{\"fromAccountId\":\"$ACC_A\",\"toAccountId\":\"$ACC_B\",\"amount\":250,\"description\":\"rent\"}")

echo "HTTP: $out"
cat /tmp/dup.json | jq . || cat /tmp/dup.json

