# Ensaio isolado do dispatcher — Etapa 5B

Preparação concluída em 23/10/2026. Nenhum evento publicado e nenhuma conexão SMTP realizada.
PostgreSQL, RabbitMQ e MailHog ficam ativos. A API não é iniciada na preparação.

## Recursos

| Recurso | Identificação |
|---|---|
| Projeto Compose | `fcg-notifications-rehearsal` |
| Imagem local | `fcg-notifications:dispatcher-rehearsal-4b3` |
| ID da imagem construída | `sha256:c9f91695f1582575d851bf299ec95940c433159f2ff01e0e72964e4b37454021` |
| Banco exclusivo | `fcg_notifications_rehearsal` |
| Usuário PostgreSQL | `notifications_rehearsal` |
| Vhost exclusivo | `/dispatcher-rehearsal` |
| Usuário RabbitMQ | `notifications_rehearsal` |
| Rede | `fcg-notifications-rehearsal_rehearsal` |
| Volume PostgreSQL | `fcg-notifications-rehearsal_postgres-data` |
| Volume RabbitMQ | `fcg-notifications-rehearsal_rabbitmq-data` |

Não existem redes/volumes externos nem conexão com os serviços do projeto `fcg-demo`.
O DNS `postgres`/`rabbitmq` resolve serviços desta rede exclusiva.
A rede permite saída para Gmail na futura Etapa 5C; não é uma barreira de egress.

| Serviço | Porta local, somente 127.0.0.1 |
|---|---|
| API, somente após Start | `5093` |
| PostgreSQL | `55442` |
| RabbitMQ AMQP | `5678` |
| RabbitMQ Management | `15678` |
| MailHog web | `8028` |

MailHog recebe SMTP em `smtp-dev:1025` dentro da rede, sem publicar essa porta no host.

## Arquivos

- `compose.yml`: quatro serviços, perfil `functional` exclusivo da API e nenhum restart automático.
- `Dockerfile`: cópia do Dockerfile atual, usada apenas para a tag do ensaio.
- `Dockerfile.dockerignore`: permite apenas projetos/configuração de build e fontes; exclui `.env`, binários locais e artefatos.
- `Manage-Rehearsal.ps1`: valida, constrói, prepara, verifica, inicia explicitamente ou para o ensaio.
- `.gitignore`: exclui credenciais locais e artefatos gerados.
- `.env`: criado localmente com senhas aleatórias de PostgreSQL/RabbitMQ; ignorado pelo Git e excluído do contexto Docker. Não é sobrescrito.
- `artifacts/migrations.sql`: SQL idempotente gerado pelo EF a partir das migrations atuais, também ignorado pelo Git.

Nenhum arquivo existente do projeto, `.env` anterior ou Compose integrado foi alterado.

## Variáveis e segredos

O helper lê o `.env` existente na raiz da NotificationsAPI, sem alterá-lo, para resolver:

- `SMTP_USER`: conta Gmail.
- `SMTP_PASSWORD`: senha de app Gmail.
- `SMTP_FROM`: remetente autorizado.

Essas variáveis também podem vir do ambiente do processo. Para outro arquivo existente, use
`-GmailEnvFile C:\caminho\arquivo.env`. Não escreva senhas na linha de comando.
Variáveis do ambiente do processo têm precedência sobre arquivos de ambiente no Compose.

O helper gera `REHEARSAL_DB_PASSWORD` e `REHEARSAL_RABBIT_PASSWORD` no novo `.env` desta pasta.
Preserve esse arquivo junto dos volumes; trocar essas variáveis não altera automaticamente senhas de bancos já inicializados.
Não exiba `docker compose config` sem filtragem: sua saída resolvida contém segredos.
Os segredos não estão na imagem, mas os administradores do Docker podem consultar o ambiente dos containers.

O Compose declara exatamente:

- `gmail`: `smtp.gmail.com:587`, `StartTls`, autenticação e remetente pelas variáveis acima.
- `mailhog`: `smtp-dev:1025`, `None`, usuário e senha vazios, remetente `notifications@fcg.test`.

Os IDs são estáveis e não devem ser renomeados/reapontados depois de persistir entregas.
A validação verifica valores resolvidos, credenciais presentes e sintaxe dos remetentes; não autentica no Gmail.

## Preparação — não inicia a API

Requisitos: PowerShell 7, Docker Desktop Linux, SDK .NET compatível com net8.0 e `dotnet ef` instalado.
O ambiente utilizado já possuía essas ferramentas. Execute na raiz da NotificationsAPI:

```powershell
.\docker\dispatcher-rehearsal\Manage-Rehearsal.ps1 -Action Validate
.\docker\dispatcher-rehearsal\Manage-Rehearsal.ps1 -Action Build
.\docker\dispatcher-rehearsal\Manage-Rehearsal.ps1 -Action Prepare
.\docker\dispatcher-rehearsal\Manage-Rehearsal.ps1 -Action Verify
```

`Build` só constrói a tag local; não faz push nem inicia containers.
`Prepare` sobe explicitamente somente os três serviços de infraestrutura. O helper gera SQL com
`dotnet ef migrations script --idempotent` e o aplica usando `psql` dentro do PostgreSQL exclusivo.
A geração EF usa uma conexão inerte e migrations/dispatcher desabilitados; não inicia a API.
A aplicação SQL usa o serviço `postgres` do projeto fixo e o banco fixo `fcg_notifications_rehearsal`.
O helper rejeita preparação com API ativa e exige banco/Inbox sem dados e broker sem mensagens/consumidores ao final.

`Database__ApplyMigrationsOnStartup=false` impede migration implícita ao iniciar a API.

## Verificações efetuadas

- Build Docker concluído; tag integrada `1.0.6` não foi sobrescrita nem publicada.
- Imagem inspecionada em container temporário sem rede, com entrypoint shell: contém `SmtpDeliveryEvidence` e `SmtpMessageId`; API não executada.
- Configuração resolvida tem os dois destinos e todos os parâmetros solicitados.
- Projeto/rede/volumes dos três containers conferidos por `docker inspect`.
- PostgreSQL exclusivo saudável, `notifications=0`, `inbox_messages=0`.
- RabbitMQ exclusivo saudável, vhost `/dispatcher-rehearsal` sem filas/mensagens/consumidores.
- Colunas `SmtpMessageId`, `SmtpAcceptance`, `SentConfirmationToken` presentes.
- Histórico confirmado:
  - `20261022133017_InitialNotificationsSchema`
  - `20261022191841_PersistentNotificationDelivery`
  - `20261023004351_NotificationReconciliationAudit`
  - `20261023155922_SmtpDeliveryEvidence`

`Verify` repete consultas somente leitura e mostra containers/filas sem revelar senhas.
Não houve consulta nem migration no banco integrado `fcg_notifications` durante esta preparação.

## Execução futura — somente Etapa 5C

O Compose já configura `Enabled=true`, BatchSize=2, PollSeconds=5, LeaseSeconds=300,
AttemptTimeoutSeconds=60, RetrySeconds=60 e MaxAttempts=3, além do consumidor de teste de compra.
Essas opções não estão em execução enquanto a API permanece parada.

Quando autorizado para a Etapa 5C:

```powershell
.\docker\dispatcher-rehearsal\Manage-Rehearsal.ps1 -Action Start
```

Esse comando valida configuração, isolamento, migration e ausência de dados/mensagens antes de iniciar a API
com o perfil `functional`, sem rebuild. Não publica eventos. A API passa a poder consumir e enviar SMTP.
Não executar agora. Não contornar o helper com `up notificationsapi`: a seleção explícita do serviço
pode ativar seu perfil e ignorar as verificações adicionais do helper.

O Start é propositalmente restrito ao primeiro ensaio vazio. Após dados de teste, uma retomada exige
avaliar estados/reservas e mensagens; não apagar a Inbox ou notificações para burlar a verificação.

## Rollback

```powershell
.\docker\dispatcher-rehearsal\Manage-Rehearsal.ps1 -Action Stop
```

Para somente os serviços deste projeto e preserva containers, volumes, migrations e evidências.
Não usa `down -v`, `prune`, downgrade de schema ou a imagem antiga. Nenhum container integrado é removido.
Durante a Etapa 5B basta parar os três serviços; a API ainda não foi iniciada.
Depois da Etapa 5C, parar não desfaz SMTP aceito. Preservar `Unknown` e investigar resultados incertos;
não voltar ao legado como estratégia de reenvio.

## Limitações

- Credenciais, conectividade, limites de envio e entrega Gmail ainda não testados.
- O teste funcional e a deduplicação real ficarão para a Etapa 5C.
- MailHog usa armazenamento padrão em memória; parar/recriar pode perder sua caixa de teste.
- Portas de administração são locais, mas acessíveis a processos do próprio computador.
- Uma nova construção da mesma tag de ensaio pode mudar seu ID; registrar o novo ID antes de repetir testes.
- O Dockerfile dedicado deve acompanhar mudanças futuras do Dockerfile principal.
