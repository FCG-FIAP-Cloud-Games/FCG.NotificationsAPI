# FCG.NotificationsAPI

Microsserviço da FCG responsável por consumir eventos de domínio e enviar notificações por SMTP.

## Entrega duplicada para MailHog e email real

Cada evento que gera uma notificação envia a mesma mensagem para dois destinos na mesma tentativa: MailHog, para inspeção local, e o servidor SMTP real configurado no `.env`, para validar a entrega ao email indicado no evento. O MailHog continua disponível em [http://localhost:8025](http://localhost:8025), e o provedor SMTP entrega a cópia à caixa real.

Os valores comuns de desenvolvimento (portas, URLs internas, bancos, RabbitMQ, MailHog e configurações JWT) já estão no Compose. Cada membro deve copiar `.env.example` para `.env` e configurar `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM` e `SMTP_SECURE_SOCKET_OPTIONS` com credenciais SMTP próprias. O remetente deve ser autorizado pelo provedor. Na porta 587 use `StartTls`; na porta 465 use `SslOnConnect`. A senha SMTP fica fora do Compose para não ser exposta no repositório. Não inclua credenciais nos eventos RabbitMQ.

Depois de configurar o `.env`, suba a composição e reconstrua Notifications para incluir o sender de múltiplos destinos:

```bash
docker compose -f docker-compose.integrated.yml up -d --build
```

O RabbitMQ repete uma mensagem caso um dos destinos falhe. Como SMTP e MailHog não compartilham uma transação, se um destino aceitar a mensagem e o outro falhar, uma repetição pode gerar cópia duplicada no destino que já aceitou. Os logs registram o resultado de cada destino separadamente.

## Compose integrado dos quatro microsserviços

O arquivo [`docker-compose.integrated.yml`](docker-compose.integrated.yml) sobe UsersAPI, CatalogAPI, PaymentsAPI e NotificationsAPI com um PostgreSQL e um RabbitMQ compartilhados. Cada API usa seu próprio banco (`fcg_users_demo`, `fcg_catalog`, `fcg_payments` e `fcg_notifications`). O serviço `postgres-init` cria os três bancos adicionais de forma idempotente; UsersAPI executa suas migrations e prepara o par de chaves JWT, e CatalogAPI aguarda UsersAPI ficar saudável antes de carregar a chave pública compartilhada. O projeto Compose se chama `fcg-integrated` e usa volumes próprios; os dados de outras stacks permanecem separados e preservados.

Para iniciar ou atualizar a composição integrada sem apagar os volumes existentes:

```bash
docker compose -f docker-compose.integrated.yml up -d --build
```

Interfaces locais:

| Serviço | URL |
|---|---|
| UsersAPI Swagger | `http://localhost:5088/swagger` |
| CatalogAPI Swagger | `http://localhost:5081/swagger` |
| PaymentsAPI Swagger | `http://localhost:5082/swagger` |
| NotificationsAPI Swagger | `http://localhost:5083/swagger` |
| RabbitMQ Management | `http://localhost:15672` |
| MailHog | `http://localhost:8025` |

O `.env.example` contém as variáveis de portas, credenciais de demonstração compartilhadas e configuração SMTP. Copie-o para `.env` e substitua as credenciais conforme necessário; o `.env` local não deve ser enviado ao Git.

### Payloads de teste no RabbitMQ Management

Os JSONs prontos ficam em [`docs/test-payloads/`](docs/test-payloads/):

| Arquivo | Exchange | Resultado esperado |
|---|---|---|
| `user-created.json` | `UserCreatedEvent` | Envia boas-vindas e grava `userId → email` |
| `payment-processed.json` | `PaymentProcessedEvent` | Envia confirmação ao email projetado para o `userId` |
| `purchase-notification-test.json` | `PurchaseNotificationTestEvent` | Envia confirmação ao email do próprio evento (Development) |
| `user-email-updated.json` | `UserEmailUpdatedEvent` | Atualiza o email projetado, sem enviar mensagem |

No painel em `http://localhost:15672`, abra a exchange indicada, deixe **Routing key** vazia, preencha **Properties** com `content_type` = `application/json` e cole o conteúdo do arquivo em **Payload**. Para testar o pagamento, publique primeiro `user-created.json`, aguarde seu processamento e então publique `payment-processed.json`; os dois arquivos usam o mesmo `userId`. O evento `PurchaseNotificationTestEvent` pode ser testado independentemente.

Os endereços `example.net` nos exemplos são marcadores: substitua-os por uma caixa controlada por você, pois cada evento também é entregue ao SMTP real. Ao repetir um evento, gere novos `eventId`, `correlationId` e IDs de pagamento/pedido; mantenha o mesmo `userId` entre cadastro e pagamento.

No ambiente local do Compose, `notificationsapi` aplica as migrations do banco ao iniciar (`Database__ApplyMigrationsOnStartup=true`). A API de produção não aplica migrations automaticamente; execute-as no processo de implantação.

Dispare um e-mail de boas-vindas para o endereço desejado:

```bash
curl -i -X POST http://localhost:5083/api/TestNotification/user-created \
  -H 'Content-Type: application/json' \
  -d '{"name":"Teste","email":"seu-email@exemplo.com"}'
```

O endpoint publica um `UserCreatedEvent` válido no RabbitMQ. A resposta confirma a publicação; consulte o MailHog e a caixa real para confirmar as duas entregas. Acompanhe os logs com `docker compose -f docker-compose.integrated.yml logs -f notificationsapi` para ver o resultado por destino.

Por padrão, o painel RabbitMQ fica acessível somente no próprio computador (`127.0.0.1`). Para permitir que colegas na mesma rede local publiquem eventos no ambiente que está rodando neste computador, configure `FCG_RABBIT_MANAGEMENT_BIND_ADDRESS=0.0.0.0` no `.env` e recrie o RabbitMQ. Eles poderão abrir `http://IP-DO-COMPUTADOR:15672` e entrar com as credenciais RabbitMQ do `.env`. Use isso somente em uma rede confiável; não exponha a porta de gerenciamento diretamente à internet. O SMTP permanece configurado apenas no serviço da API, portanto não é necessário compartilhar a senha de app do Gmail com quem apenas publica eventos.

### Publicar diretamente pelo RabbitMQ Management

O consumer `notifications-user-created` aceita JSON simples (`application/json`) para permitir este teste. Abra `http://localhost:15672`, entre com `RABBITMQ_USER` e `RABBITMQ_PASSWORD` do `.env`, vá a **Exchanges** → `UserCreatedEvent` → **Publish message**, deixe a routing key vazia e informe `application/json` em **Properties → content_type**. Use um corpo válido como:

```json
{
  "eventId": "e6d32c72-e339-4a2e-bdb9-48a7de0aef51",
  "correlationId": "a98034b4-840a-44af-a0e0-29400dcc379a",
  "occurredAt": "2026-10-19T12:00:00Z",
  "version": 1,
  "userId": "ae4b3272-d514-43cb-8763-91f1a5d8bc07",
  "name": "Teste direto RabbitMQ",
  "email": "email-informado-para-o-teste@exemplo.com"
}
```

Confirme em **Queues and Streams** → `notifications-user-created` que existe um consumer. O `userId` e o `email` publicados serão projetados no banco do Notifications e o endereço receberá a mensagem de boas-vindas tanto no MailHog quanto no provedor real.

O `UserCreatedEvent` também atualiza a projeção local `user_email_contacts`, usada pelo fluxo real de confirmação de compra. Assim, `PaymentProcessedEvent` continua contendo apenas `UserId`; Notifications resolve o endereço sem espalhar e-mail pelo evento financeiro. O UsersAPI analisado ainda precisa publicar `UserEmailUpdatedEvent` quando o endereço mudar para manter essa projeção atualizada. Até essa mudança existir no produtor, novos cadastros já abastecem a projeção.

### Testar confirmação de compra com destinatário livre

Em Development, publique JSON no exchange `PurchaseNotificationTestEvent` (routing key vazia, `content_type=application/json`). Esse evento de teste tem exchange e fila próprios (`notifications-purchase-test`) e aceita o e-mail destinatário no próprio JSON. Ele exercita o envio de confirmação no Notifications sem alterar o contrato financeiro consumido também pelo Catalog:

```json
{
  "eventId": "dbfe6954-a2c6-43bb-bbcc-ffb146f861bb",
  "correlationId": "f96e5461-839e-4700-8bf9-6437ad7d5ca8",
  "occurredAt": "2026-10-22T12:00:00Z",
  "version": 1,
  "userId": "ae4b3272-d514-43cb-8763-91f1a5d8bc07",
  "orderId": "4c11d019-a8d4-4538-b9da-a201da72045e",
  "gameId": "574ad612-723c-4b4e-9f65-02f5a087ae54",
  "amount": 59.90,
  "currency": "BRL",
  "email": "destinatario-escolhido@exemplo.com"
}
```

Esse evento só é registrado quando `Messaging:EnablePurchaseNotificationTestEndpoint=true`, configuração habilitada em `appsettings.Development.json`. Confira a cópia no MailHog e no endereço real informado no JSON.

Esse teste valida o envio e o destinatário variáveis no Notifications, mas não simula um pagamento real. Para verificar o caminho de compra, crie/publique primeiro um `UserCreatedEvent` para popular `user_email_contacts`; depois publique um `PaymentProcessedEvent` aprovado usando o mesmo `userId`. Esse evento financeiro não deve conter `email`.

Para preparar somente a projeção sem enviar boas-vindas, o Notifications também consome `UserEmailUpdatedEvent` no exchange `UserEmailUpdatedEvent`, com os campos `eventId`, `correlationId`, `occurredAt`, `version`, `userId` e `email`. Esse contrato já é aceito pelo Notifications, mas o UsersAPI ainda precisa ser atualizado para publicá-lo quando um e-mail for alterado.

## Eventos

| Evento | Uso |
|---|---|
| `UserCreatedEvent` | Projetar o e-mail do usuário e enviar boas-vindas |
| `UserEmailUpdatedEvent` | Atualizar o e-mail na projeção do Notifications |
| `PaymentProcessedEvent` | Se `Approved`, resolver destinatário pelo `UserId` e enviar confirmação |
| `PurchaseNotificationTestEvent` | Testar confirmação com destinatário do JSON, somente em Development |

O serviço reage a eventos e não acessa diretamente os bancos de Users, Catalog ou Payments. A tabela `user_email_contacts` é uma projeção própria do Notifications.

## Tecnologia

- .NET 8 (ASP.NET Core)
- **MassTransit + RabbitMQ** (Card 06 — #49), com consumers para cadastro, alteração de e-mail, pagamento e teste local.
- Consumers executam em background pelo host genérico (`IHostedService`, via MassTransit).

## Estrutura

```text
src/
├── FCG.Notifications.Api/             Host, DI, /health, ProblemDetails
├── FCG.Notifications.Application/     Casos de uso, abstrações (INotificationSender), contratos de eventos (Card 05)
├── FCG.Notifications.Domain/          Domínio de notificações (sem dependências)
└── FCG.Notifications.Infrastructure/  MassTransit/RabbitMQ, entrega, dados e IoC
tests/
├── FCG.Notifications.UnitTests/
└── FCG.Notifications.IntegrationTests/
```

Direção das dependências: `Api → Application, Infrastructure`; `Infrastructure → Application, Domain`; `Application → Domain`.

## Como executar

```bash
dotnet build
dotnet run --project src/FCG.Notifications.Api
curl -i http://localhost:5210/health
dotnet test
```

O `/health` confirma que o processo está ativo, não que RabbitMQ, PostgreSQL ou SMTP estão prontos. A conexão do MassTransit é assíncrona (`MassTransitHostOptions.WaitUntilStarted = false`), então falhas do broker aparecem nos logs sem bloquear o startup. No Compose, o PostgreSQL é aguardado e as migrations são aplicadas antes da API iniciar.

Para testar a conexão de verdade em ambiente local, suba um RabbitMQ avulso (fora deste repositório, que não cria Dockerfile/Compose por escopo):

```bash
docker run -d --name rabbitmq-local -p 5672:5672 -p 15672:15672 rabbitmq:3-management
dotnet user-secrets init --project src/FCG.Notifications.Api
dotnet user-secrets set "RabbitMq:Username" "guest" --project src/FCG.Notifications.Api
dotnet user-secrets set "RabbitMq:Password" "guest" --project src/FCG.Notifications.Api
```

## Configuração

`appsettings.json` e `appsettings.Development.json`, sem segredos reais. A seção `RabbitMq` (`Host`, `VirtualHost`, `Username`, `Password`) vem em branco no repositório; valores reais chegam por variáveis de ambiente, secrets locais (`dotnet user-secrets`) ou Kubernetes Secrets. A credencial é própria deste serviço — nunca o JWT do usuário (seção 14 do documento do Card 06/#49).

## Reverter a decisão de mensageria

Se o grupo desaprovar MassTransit/RabbitMQ.Client:

1. Remover as duas linhas de `MassTransit`/`MassTransit.RabbitMQ` de `Directory.Packages.props`.
2. Remover o `<PackageReference>` correspondente de `src/FCG.Notifications.Infrastructure/FCG.Notifications.Infrastructure.csproj`.
3. Remover `src/FCG.Notifications.Infrastructure/Messaging/RabbitMqOptions.cs` e o conteúdo de `IoC/DependencyInjection.cs` referente ao `AddMassTransit`/`UsingRabbitMq`, voltando a um `return services;` vazio.
4. Remover a seção `RabbitMq` de `appsettings.json`.
5. Remover `tests/FCG.Notifications.UnitTests/MassTransitRegistrationTests.cs`.
6. Os contratos em `Application/Messaging/Contracts` (Card 05) **não dependem da biblioteca** e podem ficar como estão.

## Status atual

O envio de boas-vindas é feito pelo consumer de `UserCreatedEvent`. O fluxo de confirmação de pagamento ainda não dispõe do e-mail do usuário no contrato do evento; para entregar essa notificação a uma caixa real, será necessário incluir/projetar o e-mail associado ao `UserId`.
