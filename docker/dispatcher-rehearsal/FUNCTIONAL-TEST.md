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
$scenario = 'destinatario-2'

# Somente leitura; possível com API parada.
python -B $tool isolation

# Preparação local: pede destinatário sem eco, gera IDs uma única vez e NÃO publica.
python -B $tool init --scenario $scenario
python -B $tool plan --scenario $scenario

# SOMENTE ETAPA 5C.2, depois de iniciar explicitamente a API com o helper da 5B:
python -B $tool publish --scenario $scenario --confirm-send

# Consultas somente leitura; aguardar processamento assíncrono.
python -B $tool db --scenario $scenario
python -B $tool mailhog --scenario $scenario
python -B $tool queues --scenario $scenario
python -B $tool logs --scenario $scenario

# Após ambas Sent e uma mensagem no MailHog, salvar baseline.
# Conferir também uma mensagem no Gmail antes da reentrega.
python -B $tool snapshot --scenario $scenario

# Uma reentrega explícita do MESMO evento/payload, sem novos IDs ou timestamps.
python -B $tool repeat --scenario $scenario --confirm-send

# Aguardar o segundo processamento do consumidor e confirmar filas drenadas.
python -B $tool queues --scenario $scenario
python -B $tool logs --scenario $scenario
python -B $tool db --scenario $scenario
python -B $tool mailhog --scenario $scenario
python -B $tool compare --scenario $scenario
```

Cada cenário novo fica em `artifacts/functional/scenarios/<nome>/`, ignorado pelo Git.
O evento contém endereço pessoal: não deve ser anexado a relatórios nem exibido. O hash SHA-256
fica ao lado. Payload, baseline, intenções e recibos de publicação/reentrega ficam na pasta do cenário.
`init` nunca sobrescreve cenário existente; `plan` mostra apenas cenário, EventId, hash e metadados.
O destinatário é solicitado por entrada oculta. Espaços nas bordas são removidos; nomes de exibição,
colchetes e espaços internos continuam rejeitados. Sem entrada oculta disponível, a criação é bloqueada.

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

- Nenhum critério funcional foi executado/aprovado nesta preparação. Os testes do helper
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

Doze testes offline cobrem contrato/hash, confirmação explícita, resposta perdida sem retry,
payload/message_id idênticos na reentrega, múltiplos cenários, seleção dos comandos, preservação
de evidências legadas, nomes inválidos, validação do destinatário e recusa de sobrescrita.


## Correção: múltiplos cenários sem perder o teste anterior

A ferramenta anterior usava uma única pasta fixa para todos os comandos. Por isso:

- `Use a plain valid recipient address`: a expressão regular rejeitava qualquer espaço, inclusive
  nas bordas, além de nome de exibição, colchetes e ausência de endereço simples. O valor rejeitado
  não foi registrado; não é possível identificar qual dessas condições ocorreu no caso relatado.
- `FileExistsError`: `event.json` era aberto com `xb` (criação exclusiva). A segunda criação era
  bloqueada para preservar o evento anterior; faltava selecionar uma pasta independente.

Agora `--scenario` é obrigatório em todos os comandos ligados a um evento, inclusive publicação.
`isolation` é a única operação sem cenário. Não há cenário ativo implícito nem escolha automática
pelo diretório mais recente. A seleção é feita a cada invocação.

Nomes aceitos: 1 a 48 caracteres, letras minúsculas, números e hífens, começando por letra.
Caminhos, nomes reservados do Windows e redirecionamentos simbólicos de cenário são rejeitados.
Use nomes neutros como `destinatario-2`, nunca o e-mail ou outro dado pessoal no nome.

`legacy` é um nome reservado que aponta diretamente para a pasta antiga `artifacts/functional/`.
Nenhum arquivo antigo é movido, renomeado, regravado ou excluído. `init --scenario legacy` é bloqueado.
O evento anterior informado pelo operador é `870cb8d1-23ca-4184-b3af-a359692803fa`.
Para voltar a suas evidências, sem publicar nada:

```powershell
python -B $tool plan --scenario legacy
python -B $tool db --scenario legacy
python -B $tool mailhog --scenario legacy
python -B $tool logs --scenario legacy
# Se já houver baseline salva para o teste anterior:
python -B $tool compare --scenario legacy
```

Para o segundo cenário, use os comandos da seção Comandos com `$scenario = 'destinatario-2'`.
Se esse nome já existir, consulte seu `plan` ou escolha um nome realmente novo; não apague arquivos.
A criação acontece somente no `init`; publicação, snapshot, repeat e compare nunca geram IDs novos.
O segundo cenário terá novos IDs. Sua própria reentrega reutiliza exatamente seus bytes de payload,
EventId, demais GUIDs, OccurredAt e message_id AMQP. Os recibos não são compartilhados entre cenários.

Os cenários compartilham o ambiente isolado de infraestrutura; a separação é por evento e arquivos.
Execute um cenário por vez. Consultas de banco, MailHog e logs filtram o evento selecionado; os
contadores RabbitMQ são da fila compartilhada e não identificam individualmente um cenário.
A criação de novo cenário não resolve nem descarta a suspeita de duplicidade no destinatário anterior.
Essa investigação deve usar as evidências preservadas e os Message-Ids; não foi executada nesta correção.

Nesta correção foram alterados somente o script, seus testes offline e este documento. Os testes usam
fixtures temporárias descartáveis em `artifacts/offline-tests`, conforme o mecanismo já existente;
nunca abrem as evidências reais em `artifacts/functional`. Nenhum Docker, broker ou SMTP foi acessado.
