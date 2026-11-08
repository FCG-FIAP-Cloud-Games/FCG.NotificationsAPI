# FCG.NotificationsAPI

Microsserviço da FCG (FIAP Cloud Games) que **consome eventos de domínio pelo RabbitMQ e envia e-mails por SMTP**: boas-vindas quando uma conta é criada e confirmação quando um pagamento é aprovado. Ele não acessa os bancos de Users, Catalog ou Payments; reage apenas a eventos e mantém uma projeção própria de e-mails.

## Início rápido

Pré-requisitos: Docker (com Compose) e uma caixa de e-mail sua para receber os testes.

```bash
cp .env.example .env            # defina SMTP_* (ou deixe SMTP_HOST=smtp-dev para usar o MailHog)
docker compose -f docker-compose.integrated.yml up -d --build
docker compose -f docker-compose.integrated.yml ps
```

Quando todos os serviços estiverem `healthy`, abra o Swagger do Users (http://localhost:5088/swagger), crie uma conta e confira o e-mail de boas-vindas. O [roteiro de teste](#roteiro-de-teste-pelo-swagger) cobre também a compra, a idempotência e o pagamento rejeitado.

## Como o fluxo funciona

```text
Cadastro
  Users API ──UserCreatedEvent──► RabbitMQ ──► Notifications
                                                 ├─ grava userId → e-mail (user_email_contacts)
                                                 └─ envia "Bem-vindo à FCG!"

Compra
  Usuário ─POST /pedidos (Idempotency-Key)─► Catalog ──► Payments
  Payments ──PaymentProcessedEvent──► RabbitMQ ──► Notifications
                                                     ├─ Approved: busca o e-mail pelo userId e envia "Compra confirmada"
                                                     └─ Rejected: encerra sem e-mail
  Payments ──PaymentProcessedEvent──► Catalog (fila própria; o Notifications não depende dele)
```

O `correlationId` acompanha o fluxo: nasce no `x-correlation-id` do pedido no Catalog, passa pelo Payments e chega ao evento. Use-o para cruzar os logs dos três serviços.

## Visão geral

| Situação | Evento consumido | Resultado |
|---|---|---|
| Conta criada | `UserCreatedEvent` | Grava `userId → e-mail` e envia **"Bem-vindo à FCG!"** |
| E-mail do usuário alterado | `UserEmailUpdatedEvent` | Atualiza a projeção, sem enviar mensagem |
| Pagamento aprovado | `PaymentProcessedEvent` (`Approved`) | Resolve o e-mail pelo `userId` e envia **"Compra confirmada"** |
| Pagamento rejeitado | `PaymentProcessedEvent` (`Rejected`) | Nenhum e-mail (por desenho) |
| Teste de compra (só Development) | `PurchaseNotificationTestEvent` | Envia a confirmação ao e-mail do próprio evento |

O `PaymentProcessedEvent` carrega só o `UserId`, nunca o e-mail. O endereço vem da projeção `user_email_contacts`, preenchida pelo `UserCreatedEvent` (e atualizada pelo `UserEmailUpdatedEvent`). Por isso **o usuário precisa existir na projeção antes da compra**.

## Arquitetura

.NET 8, ASP.NET Core, MassTransit 8 com RabbitMQ, EF Core 8 com PostgreSQL (Npgsql) e MailKit para SMTP.

```text
src/
├── FCG.Notifications.Api/             Host, controllers, Swagger, /health, /info
├── FCG.Notifications.Application/     Casos de uso, contratos de eventos, abstrações
├── FCG.Notifications.Domain/          Entidades e enums (sem dependências)
└── FCG.Notifications.Infrastructure/  MassTransit/RabbitMQ, dados, entrega SMTP, IoC
tests/
├── FCG.Notifications.UnitTests/
└── FCG.Notifications.IntegrationTests/
docs/                                  Reconciliação manual e payloads de teste
docker/                                Scripts do compose e ensaio do dispatcher
k8s/                                   Manifestos do Notifications
```

Dependências: `Api → Application, Infrastructure`; `Infrastructure → Application, Domain`; `Application → Domain`. Os pacotes usam gerenciamento central (`Directory.Packages.props`) e o build trata avisos como erros.

## Eventos, exchanges e filas

Os nomes dos exchanges são fixados com `SetEntityName` (não usam o namespace completo do tipo .NET).

| Evento | Exchange | Fila do Notifications | Produtor |
|---|---|---|---|
| `UserCreatedEvent` | `UserCreatedEvent` | `notifications-user-created` | UsersAPI |
| `UserEmailUpdatedEvent` | `UserEmailUpdatedEvent` | `notifications-user-email-updated` | UsersAPI (ainda a implementar) |
| `PaymentProcessedEvent` | `PaymentProcessedEvent` | `notifications-payment-processed` | PaymentsAPI |
| `PurchaseNotificationTestEvent` | `PurchaseNotificationTestEvent` | `notifications-purchase-test` | Testes (Development) |

Pontos importantes:

- **Desserialização JSON simples.** Os endpoints usam `UseRawJsonDeserializer`, então qualquer produtor que publique `application/json` com os campos do contrato funciona. O envelope do MassTransit, quando existir, precisa ter o URN igual ao namespace do contrato; caso contrário a mensagem vai para a fila `_skipped`.
- **Exchange do UsersAPI.** O UsersAPI publica no exchange `FCG.Notifications.Application.Messaging.Contracts:UserCreatedEvent`. O serviço `rabbitmq-bindings` (abaixo) liga esse exchange ao `UserCreatedEvent`.
- **Exchange do PaymentsAPI.** O script de bindings também suporta ligar o exchange real do Payments ao `PaymentProcessedEvent`, caso o nome seja diferente. Confirme o nome na aba *Exchanges* do RabbitMQ.
- **Retentativas.** Falhas transitórias são reprocessadas em 5 s, 15 s, 30 s, 60 s e 2 min. Depois disso a mensagem vai para a fila `<fila>_error`. Erros permanentes (`PermanentMessageException`, como evento inválido) não são repetidos.

### Contratos

Todos herdam `IEventEnvelope` (`EventId`, `CorrelationId`, `OccurredAt`, `Version`). Exemplos em [`docs/test-payloads/`](docs/test-payloads/).

- `UserCreatedEvent`: `userId`, `name`, `email`. O e-mail é validado com `MailAddress`.
- `UserEmailUpdatedEvent`: `userId`, `email`.
- `PaymentProcessedEvent`: `paymentId`, `orderId`, `userId`, `gameId`, `amount`, `currency`, `status` (`"Approved"` ou `"Rejected"`, como texto).
- `PurchaseNotificationTestEvent`: os campos da compra mais `email`.

## Modos de entrega

**Direto (padrão).** O consumer grava a projeção de e-mail e envia por SMTP na hora. A idempotência fica a cargo do broker: repetir a mesma mensagem pode reenviar o e-mail.

**Persistente.** Ative com `NotificationsDispatcher__Enabled=true`. Os consumers gravam o evento numa caixa de entrada (`inbox_messages`, chave `EventId + ConsumerName`) e cria uma linha em `notifications`; um dispatcher em background envia depois. Resultado: **o mesmo evento nunca gera dois e-mails**, mesmo com redelivery, e o histórico fica auditável.

Requisitos do modo persistente:

- `Smtp__Id` obrigatório, único e com até 100 caracteres. A aplicação falha no startup se estiver ausente ou duplicado.
- Parâmetros do dispatcher (`NotificationsDispatcher__*`):

| Parâmetro | Padrão | Função |
|---|---|---|
| `PollSeconds` | 5 | Intervalo de leitura |
| `BatchSize` | 20 | Tamanho do lote (1–1000) |
| `LeaseSeconds` | 300 | Reserva da notificação (precisa ser maior que `AttemptTimeoutSeconds + 30`) |
| `AttemptTimeoutSeconds` | 60 | Tempo máximo por envio |
| `RetrySeconds` | 60 | Espera antes de nova tentativa |
| `MaxAttempts` | 3 | Tentativas até `Failed` |

Estados de `notifications.Status`: `Pending`, `Preparing`, `Sending`, `Sent`, `Failed` e `Unknown` (envio com resultado incerto, tratado por [reconciliação](#operação-reconciliação-e-ensaio-do-dispatcher)). Tipos: `Welcome`, `PurchaseConfirmation` e `PurchaseConfirmationTest`.

## Modelo de dados e garantias

Banco próprio do Notifications (PostgreSQL), criado por migrations:

| Tabela | Função | Restrições relevantes |
|---|---|---|
| `user_email_contacts` | Projeção `userId → e-mail` | Chave primária `UserId`; e-mail até 320 caracteres |
| `inbox_messages` | Eventos já processados por consumer | Índice único `EventId + ConsumerName` |
| `notifications` | Cada e-mail a enviar, com status, tentativas e destino SMTP | Índice único `BusinessOperationId + Type + LogicalRecipientId + SmtpDestinationId` |
| `notification_reconciliation_audits` | Histórico das decisões manuais | Índice único por `ActionId`; sem UPDATE, DELETE nem TRUNCATE |

O que isso garante no [modo persistente](#modos-de-entrega):

- **Um e-mail por evento.** O mesmo `eventId` não é processado duas vezes pelo mesmo consumer.
- **Um e-mail por operação de negócio.** O índice único da tabela `notifications` impede duplicar a notificação de um mesmo pedido, tipo e destinatário.
- **Resultado de pagamento consistente.** Dois eventos do mesmo `paymentId` com resultados ou dados conflitantes são tratados como erro permanente e não geram e-mail.
- **Falha de envio recuperável.** O dispatcher reserva a notificação (lease), tenta de novo conforme `MaxAttempts` e marca `Unknown` quando não consegue provar se o SMTP aceitou a mensagem.

No modo direto essas garantias não existem; veja [Limitações](#limitações-conhecidas).

## Endpoints HTTP

O serviço é basicamente um consumidor de eventos. A API expõe:

| Método e rota | Descrição |
|---|---|
| `GET /health` | O processo está ativo. **Não** verifica RabbitMQ, PostgreSQL nem SMTP. |
| `GET /info` | Nome e versão do serviço. |
| `POST /api/TestNotification/user-created` | Publica um `UserCreatedEvent` válido. Disponível só com `Messaging__EnableTestEndpoints=true`; caso contrário responde 404. |

Corpo do endpoint de teste: `{"name": "Teste", "email": "voce@exemplo.com"}`. Responde 400 para nome vazio ou e-mail inválido e 200 com os IDs do evento publicado.

O Swagger fica ligado em Development ou quando `Swagger__Enabled=true`.

## Configuração

Valores via `appsettings*.json`, variáveis de ambiente (dois underscores: `RabbitMq__Host`), `dotnet user-secrets` ou Kubernetes Secrets. **Nenhuma credencial real deve ser versionada.**

| Variável | Descrição |
|---|---|
| `RabbitMq__Host`, `__Port`, `__VirtualHost`, `__Username`, `__Password`, `__UseTls` | Conexão com o broker. `Host` é só o nome (ex.: `rabbitmq`), sem `amqp://`. |
| `ConnectionStrings__NotificationsDatabase` | PostgreSQL do Notifications. |
| `Database__ApplyMigrationsOnStartup` | `true` aplica as migrations ao iniciar (compose e k8s de demonstração). Em produção, aplique no processo de implantação. |
| `Smtp__Host`, `__Port`, `__User`, `__Password`, `__From`, `__SecureSocketOptions` | Destino SMTP. Gmail na 587 usa `StartTls`; na 465 usa `SslOnConnect`; MailHog usa `None`. |
| `Smtp__Id` | Identificador do destino SMTP (obrigatório no modo persistente). |
| `Smtp__Destinations__<nome>__*` | Opcional: vários destinos SMTP nomeados (`Host`, `Port`, `From`, ...), cada um com seu `Id`. Sem essa seção, vale o bloco `Smtp__*`. |
| `NotificationsDispatcher__Enabled` | Liga o modo persistente. |
| `Messaging__EnableTestEndpoints` | Habilita `POST /api/TestNotification/user-created`. |
| `Messaging__EnablePurchaseNotificationTestEndpoint` | Registra o consumer de `PurchaseNotificationTestEvent` (ligado em `appsettings.Development.json`). |
| `Swagger__Enabled` | Força o Swagger fora de Development. |

## Executando localmente

```bash
dotnet build
dotnet run --project src/FCG.Notifications.Api
curl -i http://localhost:5210/health
```

A conexão do MassTransit é assíncrona (`WaitUntilStarted = false`): falhas do broker aparecem nos logs sem bloquear o startup. Para ter RabbitMQ localmente:

```bash
docker run -d --name rabbitmq-local -p 5672:5672 -p 15672:15672 rabbitmq:3-management
dotnet user-secrets set "RabbitMq:Username" "guest" --project src/FCG.Notifications.Api
dotnet user-secrets set "RabbitMq:Password" "guest" --project src/FCG.Notifications.Api
```

## Compose integrado dos quatro microsserviços

[`docker-compose.integrated.yml`](docker-compose.integrated.yml) sobe UsersAPI, CatalogAPI, PaymentsAPI e NotificationsAPI com um PostgreSQL e um RabbitMQ compartilhados (projeto `fcg-integrated`, volumes próprios). Cada API tem seu banco: `fcg_users_demo`, `fcg_catalog`, `fcg_payments` e `fcg_notifications`.

```bash
cp .env.example .env      # ajuste o SMTP
docker compose -f docker-compose.integrated.yml up -d --build
```

Serviços auxiliares do compose:

- `postgres-init` cria os bancos adicionais de forma idempotente.
- `rabbitmq-bindings` (`docker/rabbitmq/create-bindings.sh`) espera a API de gerenciamento, declara os exchanges e cria os bindings entre o exchange longo do UsersAPI e o `UserCreatedEvent` (e o do Payments, se diferente). Termina com sucesso e roda de novo a cada `up`. Variáveis: `USERS_USER_CREATED_EXCHANGE` e `PAYMENTS_PAYMENT_PROCESSED_EXCHANGE`.
- `smtp-dev` (MailHog) recebe e-mails de teste.

Interfaces locais:

| Serviço | URL |
|---|---|
| UsersAPI Swagger | http://localhost:5088/swagger |
| CatalogAPI Swagger | http://localhost:5081/swagger |
| PaymentsAPI Swagger | http://localhost:5082/swagger |
| NotificationsAPI Swagger | http://localhost:5083/swagger |
| RabbitMQ Management | http://localhost:15672 |
| MailHog | http://localhost:8025 |

Variáveis do `.env` (veja `.env.example`): portas, credenciais de demonstração, `NOTIFICATIONS_API_TAG`, `NOTIFICATIONS_DISPATCHER_ENABLED`, `SMTP_DESTINATION_ID` e o bloco `SMTP_*`. O `.env` não é versionado.

**Um destino SMTP por execução.** `SMTP_HOST=smtp-dev` usa o MailHog; para Gmail configure o bloco `SMTP_*` e recrie a API:

```bash
docker compose -f docker-compose.integrated.yml up -d --force-recreate notificationsapi
```

**Apenas uma stack por vez.** O compose do repositório de infra (`fcg-demo`) usa as mesmas portas. Pare uma antes de subir a outra (`docker compose ... down`, sem `-v`, preserva os dados).

Para colegas na rede local acessarem o painel do RabbitMQ, use `FCG_RABBIT_MANAGEMENT_BIND_ADDRESS=0.0.0.0` somente em rede confiável.

## Roteiro de teste pelo Swagger

Tudo é feito pelos Swaggers e pelo painel do RabbitMQ, sem terminal.

### 1. E-mail de boas-vindas

1. Users: crie um usuário com um e-mail que você consiga abrir (ou use `POST /api/TestNotification/user-created` no Notifications).
2. RabbitMQ → **Queues**: `notifications-user-created` recebe 1 mensagem e volta a `0`. As filas `_error` e `_skipped` ficam vazias.
3. A caixa de e-mail recebe **"Bem-vindo à FCG!"**. Isso também popula a projeção usada pela compra.

### 2. E-mail de compra confirmada

1. **Criar um jogo (admin, uma vez).** No Users, `POST /api/v1/auth/login` com o administrador de demonstração. No Catalog, **Authorize** com esse token e `POST /api/v1/jogos`. Use preço **abaixo de 100,00** para o pagamento ser aprovado e `faixaEtaria` curta, como `"12"`.
2. **Comprar como usuário comum.** Faça login com o usuário do passo anterior e, no Catalog, use **Authorize → Logout** e cole o novo token. Depois `POST /api/v1/pedidos`:
   - Header `Idempotency-Key`: um UUID novo, diferente do `jogoId`.
   - Corpo: `{"jogoId": "<id real do jogo>"}`. O `jogoId` deve vir de `GET /api/v1/jogos`, não do exemplo do Swagger.
   - Resposta esperada: **202** com `status: "PendingPayment"`.
3. **Pagamento.** No Payments, `GET /api/payments` (sem parâmetros: só **Execute**) mostra o pedido como `Approved`. `GET /api/payment-outbox` mostra o evento publicado.
4. **RabbitMQ.** `notifications-payment-processed` consome 1 mensagem e volta a `0`; `_error` e `_skipped` ficam vazias.
5. **E-mail.** A caixa do usuário recebe **"Compra confirmada"**.

### 3. Casos extras

| Cenário | Como fazer | Esperado |
|---|---|---|
| Idempotência | Repetir o `POST /api/v1/pedidos` com o mesmo `Idempotency-Key` e corpo | Mesmo pedido, sem novo pagamento nem segundo e-mail |
| Pagamento rejeitado | Comprar um jogo com preço acima do limite do Payments | `Rejected` e nenhum e-mail |
| Compra sem usuário projetado | Publicar um pagamento de `userId` desconhecido | Mensagem em `_error` ("Não há e-mail projetado…") |
| Evento duplicado | Publicar o mesmo `eventId` duas vezes com `NotificationsDispatcher__Enabled=true` | Um único e-mail |

O limite de aprovação do Payments (`ApprovalLimit`) é 100,00 no compose integrado.

## Testes pelo RabbitMQ Management

Payloads prontos em [`docs/test-payloads/`](docs/test-payloads/):

| Arquivo | Exchange | Resultado |
|---|---|---|
| `user-created.json` | `UserCreatedEvent` | Boas-vindas e projeção `userId → e-mail` |
| `payment-processed.json` | `PaymentProcessedEvent` | Confirmação ao e-mail projetado para o `userId` |
| `purchase-notification-test.json` | `PurchaseNotificationTestEvent` | Confirmação ao e-mail do próprio JSON (Development) |
| `user-email-updated.json` | `UserEmailUpdatedEvent` | Atualiza a projeção, sem e-mail |

Em **Exchanges** → exchange desejado → **Publish message**: deixe a *routing key* vazia, defina `content_type = application/json` e cole o JSON em *Payload*. Para o pagamento, publique antes o `user-created.json` (mesmo `userId`). Ao repetir um evento, troque `eventId`, `correlationId` e os IDs de pedido e pagamento. Os endereços `example.net` são marcadores: troque por uma caixa sua.

### Como ler as filas no RabbitMQ

| Fila | Significado |
|---|---|
| `notifications-*` com mensagens paradas | O consumer está lento, parado ou sem conexão; veja `docker compose ... logs notificationsapi` |
| `notifications-*_error` | Falhou após todas as tentativas ou por erro permanente. Use **Get messages** e leia o cabeçalho de exceção |
| `notifications-*_skipped` | A mensagem chegou, mas não corresponde a nenhum consumer (URN ou contrato diferente) |
| Sem nenhuma fila `notifications-*` | A API ainda não conectou ao broker ou o `rabbitmq-bindings` não rodou |

## Testes automatizados e CI

```bash
dotnet test tests/FCG.Notifications.UnitTests/FCG.Notifications.UnitTests.csproj
dotnet test tests/FCG.Notifications.IntegrationTests/FCG.Notifications.IntegrationTests.csproj
```

Os projetos de teste são executados diretamente pelo `.csproj`, porque o `.slnx` exige o SDK 9.0.200 ou superior. Os testes são xUnit v3.

Os testes de persistência precisam de PostgreSQL e usam a variável `FCG_NOTIFICATIONS_TEST_POSTGRES`:

```bash
docker run -d --name pg-test -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:16-alpine
export FCG_NOTIFICATIONS_TEST_POSTGRES="Host=localhost;Port=5432;Username=postgres;Password=postgres"
```

Sem essa variável, esses testes são **ignorados** (skipped) em vez de falhar. Com `FCG_REQUIRE_POSTGRES_TESTS=true` a ausência da variável vira falha, o que evita um CI "verde" sem ter rodado nada. O workflow [`.github/workflows/ci.yml`](.github/workflows/ci.yml) roda os dois projetos com um serviço `postgres:16-alpine` e essa exigência ligada.

Cobertura principal: consumers, validadores (incluindo e-mail inválido), serviços de boas-vindas e de compra, compatibilidade do contrato `PaymentProcessedEvent`, registro do MassTransit, modelo de persistência, dispatcher persistente, consulta operacional e reconciliação.

## Imagem Docker

Imagem pública: `fgcprojetofiap/notifications-api` (atualmente **1.0.8**). O `Dockerfile` é multi-arquitetura (`amd64` e `arm64`). Para publicar:

```bash
docker buildx create --name fcg-builder --driver docker-container --use --bootstrap
docker buildx build --platform linux/amd64,linux/arm64 \
  -t fgcprojetofiap/notifications-api:1.0.8 --push .
```

O driver `docker-container` é necessário para build multi-plataforma. Em Apple Silicon, imagens apenas `amd64` rodam emuladas e podem demorar ou travar (segfault); prefira imagens multi-arquitetura.

## Kubernetes

[`k8s/notifications-api/notifications-api-deployment.yml`](k8s/notifications-api/notifications-api-deployment.yml) traz ConfigMap, Deployment e Service. O ConfigMap define `Swagger__Enabled`, `Messaging__EnableTestEndpoints`, `Database__ApplyMigrationsOnStartup` e `Smtp__Id`.

- O usuário e a senha SMTP vêm do secret `notifications-api-secrets` (chaves `Smtp__User` e `Smtp__Password`), **criado no cluster e fora do Git**:

  ```bash
  read -s SMTP_PWD
  kubectl create secret generic notifications-api-secrets -n fcg-demo \
    --from-literal=Smtp__User='conta@exemplo.com' \
    --from-literal=Smtp__Password="$SMTP_PWD"
  unset SMTP_PWD
  ```

- O repositório de infra (`FCG-FIAP-Repo-Infra`) contém o conjunto completo (Postgres, RabbitMQ, Job de bindings, APIs). O Service do Notifications expõe a porta 80.
- Acesso local: `kubectl port-forward -n fcg-demo svc/notifications-api 5083:80`.
- Mantenha a tag da imagem alinhada com a publicada (hoje 1.0.8).

## Operação: reconciliação e ensaio do dispatcher

- [`docs/manual-reconciliation.md`](docs/manual-reconciliation.md): como tratar notificações em `Unknown` ou `Failed`, com auditoria append-only (triggers bloqueiam UPDATE, DELETE e TRUNCATE). Não há endpoint HTTP de reconciliação; ele é uma API interna que exige uma camada administrativa autenticada. O reenvio de `Unknown` é bloqueado por padrão, porque o provedor pode já ter entregue a mensagem.
- [`docker/dispatcher-rehearsal/`](docker/dispatcher-rehearsal/): ambiente isolado para ensaiar o dispatcher (banco, vhost e portas próprios).

## Solução de problemas

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| Boas-vindas não chega | Exchange do UsersAPI sem binding para `UserCreatedEvent` | Confira o serviço `rabbitmq-bindings` e a aba *Exchanges* |
| Mensagem em `*_skipped` | URN do envelope diferente do contrato | Ajuste o produtor ou publique JSON simples |
| Mensagem em `*_error` com "Não há e-mail projetado" | Compra antes do cadastro na projeção | Crie o usuário (ou publique `UserEmailUpdatedEvent`) e reenvie |
| Pagamento aprovado e nenhum e-mail | Exchange do Payments diferente de `PaymentProcessedEvent` | Veja o nome real em *Exchanges* e configure `PAYMENTS_PAYMENT_PROCESSED_EXCHANGE` |
| Pagamento `Rejected` | Preço acima do limite | Comportamento esperado (sem e-mail) |
| API falha ao subir no modo persistente | `Smtp__Id` ausente ou duplicado | Defina `Smtp__Id` único |
| `password authentication failed` | Volume antigo do Postgres sem os bancos | Rode `01-create-databases.sh` (idempotente) no container do Postgres |
| Swagger do Catalog carrega sem parar | Segfault do `qemu` (imagem `amd64` no Apple Silicon) | Reinicie o container; considere ativar o Rosetta no Docker Desktop |
| `POST /api/v1/pedidos` retorna 400 | Falta `jogoId` ou `Idempotency-Key` | Envie ambos; a chave é um UUID próprio |
| `POST /api/v1/jogos` retorna 403 | Token sem perfil de admin | Use o token do administrador |
| `POST /api/v1/jogos` retorna 500 (`varchar(2)`) | `faixaEtaria` longa demais | Use `"12"`, `"16"` etc. |
| Aba *Queues* do RabbitMQ 4.x com `QUEUE_TYPE is not defined` | Bug da interface | Use o RabbitMQ 3-management do compose integrado |

Para ver os logs: `docker compose -f docker-compose.integrated.yml logs -f notificationsapi`.

## Segurança

- Não versione senhas SMTP, senhas de app do Gmail nem segredos de cluster. O `.env` fica no `.gitignore`; se ele já foi versionado antes, confirme com `git ls-files` e remova do índice (`git rm --cached`).
- As credenciais do compose e do `appsettings` são **apenas de demonstração**.
- A senha SMTP fica só no serviço da API e nunca trafega em eventos.
- Se uma senha de app foi publicada, revogue e gere outra: apagar o arquivo não limpa o histórico do Git.
- O painel do RabbitMQ escuta em `127.0.0.1` por padrão.

## Limitações conhecidas

- O UsersAPI ainda precisa publicar `UserEmailUpdatedEvent` quando o e-mail mudar; o Notifications já aceita o contrato.
- O `/health` não checa as dependências.
- Sem o modo persistente, não há idempotência por evento.
- Os manifestos de Kubernetes do Notifications são de demonstração: banco com migrations automáticas e Swagger ligado.
