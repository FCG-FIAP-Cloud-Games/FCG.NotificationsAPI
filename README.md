# FCG.NotificationsAPI

Microsserviço da FCG responsável por consumir eventos de domínio e enviar notificações por SMTP.

## Envio real de e-mail no Docker Compose

Para entrega externa, configure no `.env` os dados SMTP reais do provedor. `SMTP_FROM` precisa ser um remetente autorizado pelo provedor. Para SMTP na porta 587 use `SMTP_SECURE_SOCKET_OPTIONS=StartTls`; para porta 465 use `SslOnConnect`. Sem sobrescritas no `.env`, o Compose usa o MailHog local como padrão de desenvolvimento.

Para validar localmente sem credenciais externas, a API pode usar o MailHog do Compose. Isso confirma que evento, consumer e envio SMTP funcionam, mas as mensagens só aparecem em `http://localhost:8025` e não chegam a caixas externas. Os valores abaixo sobrescrevem temporariamente os valores SMTP do `.env` para esse comando:

```bash
SMTP_HOST=smtp-dev SMTP_PORT=1025 SMTP_USER= SMTP_PASSWORD= SMTP_FROM=no-reply@fcgnotifications.local SMTP_SECURE_SOCKET_OPTIONS=None docker compose up -d --force-recreate notificationsapi
```

Para voltar ao envio externo depois do teste local, recrie `notificationsapi` sem esses overrides, usando as variáveis SMTP válidas do `.env`.

Depois recrie o container para carregar as variáveis:

```bash
docker compose up -d --build --force-recreate notificationsapi
```

Dispare um e-mail de boas-vindas para o endereço desejado:

```bash
curl -i -X POST http://localhost:5083/api/TestNotification/user-created \
  -H 'Content-Type: application/json' \
  -d '{"name":"Teste","email":"seu-email@exemplo.com"}'
```

O endpoint publica um `UserCreatedEvent` válido no RabbitMQ. A resposta confirma a publicação; a entrega depende do consumidor e do SMTP configurado. Consulte os logs com `docker compose logs -f notificationsapi` se o consumidor ou o SMTP reportar falha. O serviço `smtp-dev` continua disponível para testes locais, mas não entrega mensagens a caixas de e-mail reais.

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
  "email": "leoloss@uorak.com"
}
```

Confirme em **Queues and Streams** → `notifications-user-created` que existe um consumer. Depois da publicação, verifique o log `UserCreatedEvent processado` e, se estiver usando MailHog, abra `http://localhost:8025`. Para entrega no Invertexto, o consumer também precisa conseguir autenticar no SMTP externo.

## Eventos

| Evento | Uso |
|---|---|
| `UserCreatedEvent` | Enviar e-mail de boas-vindas |
| `PaymentProcessedEvent` | Se `Approved`, processar confirmação de compra |

O serviço reage a eventos: não coordena outros serviços e não acessa os bancos de Users, Catalog ou Payments.

## Tecnologia

- .NET 8 (ASP.NET Core)
- **MassTransit + RabbitMQ** (Card 06 — #49): decisão **assumida como aprovada**. O bus já está registrado em `Infrastructure/IoC/DependencyInjection.cs`, mas **sem nenhum consumer** — isso entra em C26/C27. Se o grupo desaprovar essa biblioteca, ver "Reverter a decisão de mensageria" abaixo.
- Consumers (quando existirem) executam em background pelo host genérico (`IHostedService`, via MassTransit)

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

O serviço sobe e o `/health` responde `200` **mesmo sem um RabbitMQ real disponível** — o endpoint ignora os health checks registrados (`Predicate = _ => false`) e a conexão do MassTransit é assíncrona (`MassTransitHostOptions.WaitUntilStarted = false`), então não bloqueia o startup. Isso é esperado nesta fase: a infraestrutura de RabbitMQ ainda será provisionada em cards futuros. Os warnings de `Connection Failed` no log são esperados e não indicam falha do serviço.

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
