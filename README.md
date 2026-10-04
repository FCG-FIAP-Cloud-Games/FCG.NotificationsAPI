# FCG.NotificationsAPI

Microsserviço da FCG responsável por consumir eventos de domínio e **simular** o envio de notificações (log/console nesta fase).

## Eventos (consumers entram em C26/C27)

| Evento | Uso |
|---|---|
| `UserCreatedEvent` | Simular e-mail de boas-vindas |
| `PaymentProcessedEvent` | Se `Approved`, simular e-mail de confirmação de compra |

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

Estrutura base (C25) com MassTransit/RabbitMQ já registrados (sem consumers) e os contratos do Card 05 (#48) já definidos. Ainda **não** há consumers, Inbox, persistência, envio simulado real, Dockerfile nem Compose — serão entregues nos cards C26, C27 e C28 e nos de infraestrutura.