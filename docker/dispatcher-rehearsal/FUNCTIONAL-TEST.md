# Etapa 5C.1 — roteiro preparado, execução somente na 5C.2

Nenhuma publicação, envio SMTP ou inicialização da API faz parte da preparação.
O script `functional-test.py` requer Python 3, Docker CLI e acesso ao daemon local.
Usa apenas a biblioteca padrão Python. Não instala pacotes nem acessa serviços externos.

## Contrato conferido no código

`FCG.Notifications.Application.Messaging.Contracts.PurchaseNotificationTestEvent`:

| Propriedade | Tipo C# | Regra verificada no consumidor/validador |
|---|---|---|
| EventId | Guid | Não vazio; chave da Inbox e operação de negócio deste teste |
| CorrelationId | Guid | Não vazio |
| OccurredAt | DateTimeOffset | Enviado como ISO 8601 com offset; sem regra adicional no validador |
| Version | int | O gerador envia 1; o validador não impõe versão específica |
| UserId | Guid | Não vazio; destinatário lógico |
| OrderId | Guid | Não vazio |
| GameId | Guid | Não vazio |
| Amount | decimal | Maior ou igual a zero |
| Currency | string | Não vazia/não apenas espaços; o gerador usa BRL |
| Email | string | Aceito por MailAddress.TryCreate; destinatário efetivo |

O gerador inclui todas as propriedades, usa Amount=19.90 e pede um endereço simples de teste
sem exibi-lo. Sua restrição de endereço simples é mais estreita que a de MailAddress, intencionalmente.
Os GUIDs são dados sintéticos; este consumidor não consulta pedidos, jogos ou usuários externos.
O e-mail deve ser uma caixa controlada pelo operador. Não se assume que o remetente Gmail é o destinatário.

O consumidor persiste Inbox e entregas na mesma transação. Sua chave é
`(EventId, PurchaseNotificationTestEventConsumer)`; as entregas usam EventId como BusinessOperationId,
UserId como LogicalRecipientId, tipo PurchaseConfirmationTest e uma linha por SmtpDestinationId.

## Formato de publicação

- Broker local: `http://127.0.0.1:15678` (Management API), vhost `/dispatcher-rehearsal`.
- Exchange: `PurchaseNotificationTestEvent`.
- Fila configurada pelo MassTransit: `notifications-purchase-test`.
- Endpoint HTTP de publicação: `/api/exchanges/%2Fdispatcher-rehearsal/PurchaseNotificationTestEvent/publish`.
- Payload AMQP: o JSON simples do contrato, `content_type=application/json`, `delivery_mode=2`.
- `message_id` AMQP igual ao EventId, routing key vazia.
- O envelope HTTP contém `properties`, `routing_key`, `payload` (string JSON) e `payload_encoding=string`.

Não é necessário envelope `application/vnd.masstransit+json`: este endpoint configura
`UseRawJsonDeserializer(RawSerializerOptions.All, isDefault: true)` e possui um único tipo de consumidor.
O script exige exchange existente e um consumidor na fila. Não declara topologia e não faz publish
antes da API ficar pronta. `routed=true` comprova roteamento, não consumo, persistência ou envio SMTP.

## Comandos

Executar na raiz da NotificationsAPI. Não executar os comandos de publish/repeat na Etapa 5C.1.

```powershell
$tool = '.\docker\dispatcher-rehearsal\functional-test.py'

# Somente leitura; possível com API parada.
python -B $tool isolation

# Preparação local: pede destinatário sem eco, gera IDs uma única vez e NÃO publica.
python -B $tool init
python -B $tool plan

# SOMENTE ETAPA 5C.2, depois de iniciar explicitamente a API com o helper da 5B:
python -B $tool publish --confirm-send

# Consultas somente leitura; aguardar processamento assíncrono.
python -B $tool db
python -B $tool mailhog
python -B $tool queues
python -B $tool logs

# Após ambas Sent e uma mensagem no MailHog, salvar baseline.
# Conferir também uma mensagem no Gmail antes da reentrega.
python -B $tool snapshot

# Uma reentrega explícita do MESMO evento/payload, sem novos IDs ou timestamps.
python -B $tool repeat --confirm-send

# Aguardar o segundo processamento do consumidor e confirmar filas drenadas.
python -B $tool queues
python -B $tool logs
python -B $tool db
python -B $tool mailhog
python -B $tool compare
```

O comando `init` não foi executado para um destinatário real na preparação. Esse endereço ainda
precisa ser escolhido na 5C.2. O evento fica em `artifacts/functional/event.json`, ignorado pelo Git;
contém endereço pessoal, não deve ser anexado a relatórios ou exibido. O hash SHA-256 é salvo ao lado.
`init` nunca sobrescreve o evento; `plan` mostra apenas EventId, hash e metadados de transporte.

`publish` e `repeat` fazem no máximo um POST cada. Cada um cria um arquivo de intenção antes da
chamada e um recibo após a resposta. Se houver timeout/resposta perdida, não apagar o arquivo nem
reexecutar automaticamente: verificar banco, logs e broker. O recibo e a intenção não são prova de SMTP.
O script não inicia a API e não contém cliente SMTP.

## Evidências sem dados pessoais

- `db`: SQL em transação REPEATABLE READ / READ ONLY no banco fixo `fcg_notifications_rehearsal`,
  executado por `psql` no container exclusivo. Filtra pelo EventId salvo e retorna Inbox
  (EventId, ConsumerName, ProcessedAt), Id da entrega, SmtpDestinationId, Status, AttemptCount,
  SmtpMessageId, SmtpAcceptance, SentConfirmationToken, SentAt, UpdatedAt e FailureCode.
  Não seleciona Destination, Subject ou Body. O UUID é validado antes de compor a consulta.
- `mailhog`: GET paginado em `127.0.0.1:8028/api/v2/messages`; correlaciona com o SmtpMessageId
  da entrega `mailhog` e imprime apenas quantidade e IDs internos capturados. Respostas completas,
  corpos e demais headers ficam somente em memória e não são exibidos/salvos.
- `queues`: somente listagem de contadores da fila de teste e suas filas de erro/skipped;
  nunca usa GET de mensagens, ACK, purge ou requeue.
- `logs`: lê logs da API desde OccurredAt, exibe apenas Id/Status/Stage das entregas deste evento
  e conta o marcador de processamento do consumidor para seu EventId. Não mostra linhas brutas,
  exceções completas nem destinatários. Se API estiver parada/ausente, informa indisponibilidade.
- `snapshot`/`compare`: preservam e comparam dados sanitizados do banco e IDs MailHog antes/depois.
  Não provam isoladamente que a reentrega foi consumida; verificar também logs e contadores.

Credenciais RabbitMQ são obtidas em memória do container exclusivo; não entram em argumentos,
arquivos do evento ou saída. Requisições HTTP usam apenas loopback, sem proxy nem redirecionamentos.
Antes das leituras/publicações, o script confere labels, rede, volumes, portas, banco e vhost.

## Critérios objetivos de aprovação na 5C.2

1. Dois recibos de publicação com `routed=true` e mesmo hash/EventId, um inicial e uma reentrega.
2. Log do consumidor para esse EventId aparece uma vez após a primeira publicação e duas após a
   reentrega; fila principal termina com ready=0 e unacknowledged=0; nenhuma mensagem em error/skipped.
3. Uma única linha Inbox para EventId + ConsumerName, ProcessedAt inalterado após a reentrega.
4. Exatamente duas entregas, uma `gmail` e outra `mailhog`, ambas `Sent`, AttemptCount=1 no ensaio limpo,
   SmtpAcceptance não nulo, SentConfirmationToken não nulo, SentAt preenchido.
5. Dois SmtpMessageIds distintos entre destinos e inalterados após a reentrega; Ids, tokens,
   timestamps, contagens e demais dados da baseline continuam iguais (`compare`).
6. Uma mensagem MailHog correlacionada, mesmo ID interno após reentrega; uma mensagem Gmail
   verificada manualmente pelo Message-Id da entrega `gmail`, sem segunda cópia após reentrega.
7. Um único `SMTP_SEND_START` por NotificationId nos logs, sem nova tentativa após reentrega.
8. Repetir leituras após pelo menos dois ciclos de polling, além de confirmar o segundo log do
   consumidor. Passagem do tempo sozinha não prova processamento.

## Riscos e limites

- Nenhum critério funcional foi executado/aprovado nesta preparação. Os quatro testes do helper
  são offline e substituem HTTP/Docker por mocks; não publicam no broker.
- A verificação de Inbox + duas linhas demonstra o resultado; a atomicidade também se apoia na
  transação explícita do consumidor/UnitOfWork. Este ensaio não injeta falha de commit.
- `Sent` é aceitação SMTP persistida, não confirmação de chegada à caixa Gmail.
- Falha de autenticação, rede, antispam e limites Gmail continuam possíveis. Não regenerar EventId
  para contornar uma falha. Se surgir Unknown, investigar sem reenvio automático.
- O script exige baseline limpa com AttemptCount=1; retries de preparação exigem diagnóstico,
  não relaxamento silencioso do critério. Snapshot das duas fontes não é uma transação distribuída.
- API/reinício, log rotation ou limpeza do MailHog podem invalidar a observação. Preservar evidências.
- O armazenamento MailHog é em memória. Não reiniciar/remover containers durante o ensaio.
- O primeiro publish deve ser o único produtor deste vhost. Não publicar eventos paralelos.
- O único serviço externo previsto no ensaio é o SMTP Gmail, utilizado pela API somente na 5C.2.

## Validação da preparação

Em 23/10/2026: API parada; banco/Inbox vazios; quatro migrations presentes; broker exclusivo sem filas;
MailHog exclusivo com total=0. Nenhum banco/fila integrado foi consultado.

```powershell
python -B .\docker\dispatcher-rehearsal\test_functional_tool.py
```

Quatro testes offline: contrato/hash imutável, bloqueio sem confirmação, resposta perdida sem retry
e preservação exata do payload/message_id na reentrega.
