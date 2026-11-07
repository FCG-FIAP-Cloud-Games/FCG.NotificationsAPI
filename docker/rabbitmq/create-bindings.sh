#!/bin/sh
# Cria (de forma idempotente) os bindings exchange -> exchange entre os eventos publicados
# por Users/Payments e os exchanges que o Notifications consome.
set -eu

api="${RABBIT_API:?}"
auth="${RABBIT_USER:?}:${RABBIT_PASS:?}"
vhost="%2F"

enc() { printf '%s' "$1" | sed 's/:/%3A/g'; }

declare_exchange() {
  curl -fsS -u "$auth" -H 'content-type: application/json' -X PUT \
    "$api/exchanges/$vhost/$(enc "$1")" \
    -d '{"type":"fanout","durable":true,"auto_delete":false}' >/dev/null
}

bind() {
  src="$1"; dst="$2"
  if [ "$src" = "$dst" ]; then
    echo "bind: origem e destino iguais ($src), nada a fazer"
    return 0
  fi
  declare_exchange "$src"
  declare_exchange "$dst"
  curl -fsS -u "$auth" -H 'content-type: application/json' -X POST \
    "$api/bindings/$vhost/e/$(enc "$src")/e/$(enc "$dst")" -d '{}' >/dev/null
  echo "bind: $src -> $dst"
}

until curl -fsS -u "$auth" "$api/overview" >/dev/null 2>&1; do
  echo "aguardando a API de gerenciamento do RabbitMQ..."
  sleep 2
done

bind "${USERS_USER_CREATED_EXCHANGE:?}" "UserCreatedEvent"
bind "${PAYMENTS_PAYMENT_PROCESSED_EXCHANGE:?}" "PaymentProcessedEvent"
echo "bindings ok"