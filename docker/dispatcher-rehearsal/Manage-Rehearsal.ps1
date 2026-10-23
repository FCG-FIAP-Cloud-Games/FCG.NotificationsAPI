[CmdletBinding()]
param(
    [ValidateSet('Validate', 'Build', 'Prepare', 'Verify', 'Start', 'Stop')]
    [string]$Action = 'Validate',
    [string]$GmailEnvFile = (Join-Path $PSScriptRoot '../../.env')
)
$ErrorActionPreference = 'Stop'
$project = 'fcg-notifications-rehearsal'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$secrets = Join-Path $PSScriptRoot '.env'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
if (-not (Test-Path -LiteralPath $secrets)) {
    # Credenciais exclusivas do ensaio. Nunca substituir arquivo existente.
    $dbPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $rabbitPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $stream = [IO.File]::Open($secrets, [IO.FileMode]::CreateNew)
    $writer = [IO.StreamWriter]::new($stream)
    try { $writer.WriteLine("REHEARSAL_DB_PASSWORD=$dbPassword"); $writer.WriteLine("REHEARSAL_RABBIT_PASSWORD=$rabbitPassword") }
    finally { $writer.Dispose() }
}
$composeArgs = @('compose', '--project-name', $project, '--env-file', $secrets)
if (Test-Path -LiteralPath $GmailEnvFile) { $composeArgs += @('--env-file', [IO.Path]::GetFullPath($GmailEnvFile)) }
$composeArgs += @('--file', (Join-Path $PSScriptRoot 'compose.yml'))
function Invoke-Compose {
    & docker @composeArgs @args
    if ($LASTEXITCODE -ne 0) { throw 'Comando Docker Compose falhou.' }
}
function Invoke-Sql([string]$Sql) {
    $Sql | & docker @composeArgs exec -T postgres psql -X -v ON_ERROR_STOP=1 -U notifications_rehearsal -d fcg_notifications_rehearsal
    if ($LASTEXITCODE -ne 0) { throw 'SQL do banco exclusivo falhou.' }
}
function Assert-Isolation {
    $ids = @(Invoke-Compose ps --all --quiet)
    foreach ($id in $ids) {
        if (-not $id) { continue }
        $inspection = & docker inspect $id | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Falha ao inspecionar container.' }
        $container = $inspection[0]
        if ($container.Config.Labels.'com.docker.compose.project' -ne $project) { throw 'Projeto inesperado.' }
        $networks = @($container.NetworkSettings.Networks.PSObject.Properties.Name)
        if ($networks.Count -ne 1 -or $networks[0] -ne "${project}_rehearsal") { throw 'Rede inesperada.' }
        foreach ($mount in $container.Mounts) {
            if ($mount.Type -eq 'volume' -and $mount.Name -notin @("${project}_postgres-data", "${project}_rabbitmq-data")) {
                throw 'Volume inesperado.'
            }
        }
    }
    Write-Host 'Isolamento confirmado: projeto, rede e volumes exclusivos.'
}
function Assert-ApiStopped {
    $running = @(Invoke-Compose ps --status running --services)
    if ($running -contains 'notificationsapi') { throw 'API ja esta ativa. Preparacao/ensaio inicial exige API parada.' }
}
function Assert-Empty {
    Invoke-Sql @'
BEGIN TRANSACTION READ ONLY;
DO $$ BEGIN
 IF current_database() <> 'fcg_notifications_rehearsal' THEN RAISE EXCEPTION 'Banco inesperado'; END IF;
 IF EXISTS (SELECT 1 FROM notifications) OR EXISTS (SELECT 1 FROM inbox_messages)
 THEN RAISE EXCEPTION 'Ensaio inicial exige notifications e Inbox vazias'; END IF;
 IF NOT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId"='20261023155922_SmtpDeliveryEvidence')
 THEN RAISE EXCEPTION 'Migration SMTP ausente'; END IF;
END $$;
COMMIT;
'@
    $queueJson = Invoke-Compose exec -T rabbitmq rabbitmqctl -q list_queues -p /dispatcher-rehearsal name messages consumers --formatter json
    $queues = @($queueJson | ConvertFrom-Json)
    if (@($queues | Where-Object { $_.messages -gt 0 -or $_.consumers -gt 0 }).Count -gt 0) {
        throw 'RabbitMQ possui mensagens ou consumidores; investigar antes de iniciar.'
    }
}
# Captura em memoria: nunca imprimir compose config, que contem senhas resolvidas.
$config = (Invoke-Compose --profile functional config --format json | Out-String) | ConvertFrom-Json
$api = $config.services.notificationsapi
$envConfig = $api.environment
if ($config.name -ne $project -or @($config.services.PSObject.Properties).Count -ne 4) { throw 'Topologia inesperada.' }
if ($api.image -ne 'fcg-notifications:dispatcher-rehearsal-4b3' -or $api.profiles -notcontains 'functional') { throw 'Imagem/perfil inesperado.' }
foreach ($service in $config.services.PSObject.Properties.Value) {
    if ((@($service.networks.PSObject.Properties.Name) -join ',') -ne 'rehearsal') { throw 'Rede de servico inesperada.' }
    foreach ($port in $service.ports) { if ($port.host_ip -ne '127.0.0.1') { throw 'Porta fora do loopback.' } }
}
$expected = @{
    'NotificationsDispatcher__Enabled'='true'; 'NotificationsDispatcher__BatchSize'='2';
    'NotificationsDispatcher__PollSeconds'='5'; 'NotificationsDispatcher__LeaseSeconds'='300';
    'NotificationsDispatcher__AttemptTimeoutSeconds'='60'; 'NotificationsDispatcher__RetrySeconds'='60';
    'NotificationsDispatcher__MaxAttempts'='3'; 'Database__ApplyMigrationsOnStartup'='false';
    'Messaging__EnablePurchaseNotificationTestEndpoint'='true';
    'RabbitMq__Host'='rabbitmq'; 'RabbitMq__VirtualHost'='/dispatcher-rehearsal';
    'Smtp__Destinations__0__Id'='gmail'; 'Smtp__Destinations__0__Host'='smtp.gmail.com';
    'Smtp__Destinations__0__Port'='587'; 'Smtp__Destinations__0__SecureSocketOptions'='StartTls';
    'Smtp__Destinations__1__Id'='mailhog'; 'Smtp__Destinations__1__Host'='smtp-dev';
    'Smtp__Destinations__1__Port'='1025'; 'Smtp__Destinations__1__SecureSocketOptions'='None';
    'Smtp__Destinations__1__User'=''; 'Smtp__Destinations__1__Password'=''
}
foreach ($key in $expected.Keys) { if ([string]$envConfig.$key -cne $expected[$key]) { throw "Configuracao divergente: $key" } }
foreach ($key in @('Smtp__Destinations__0__User','Smtp__Destinations__0__Password','Smtp__Destinations__0__From')) {
    if ([string]::IsNullOrWhiteSpace($envConfig.$key)) { throw "Variavel obrigatoria ausente: $key" }
}
foreach ($index in 0,1) {
    try { $null = [Net.Mail.MailAddress]::new($envConfig."Smtp__Destinations__${index}__From") }
    catch { throw 'Remetente SMTP invalido (valor omitido).' }
}
if ($envConfig.ConnectionStrings__NotificationsDatabase -notmatch '^Host=postgres;Port=5432;Database=fcg_notifications_rehearsal;') { throw 'Banco inesperado.' }
Write-Host 'Configuracao validada: gmail/STARTTLS/autenticado e mailhog/None/sem autenticacao. Nenhuma conexao SMTP.'

switch ($Action) {
    'Validate' { }
    'Build' { Invoke-Compose --profile functional build notificationsapi }
    'Prepare' {
        Assert-ApiStopped
        Invoke-Compose up -d --wait postgres rabbitmq smtp-dev
        Assert-Isolation
        New-Item -ItemType Directory -Force $artifacts | Out-Null
        $sqlPath = Join-Path $artifacts 'migrations.sql'
        $previousConnection = $env:ConnectionStrings__NotificationsDatabase
        $previousMigrations = $env:Database__ApplyMigrationsOnStartup
        $previousDispatcher = $env:NotificationsDispatcher__Enabled
        try {
            # Geracao de SQL, sem executar a API nem conectar a banco pelo EF.
            $env:ConnectionStrings__NotificationsDatabase = 'Host=127.0.0.1;Port=1;Database=fcg_notifications_rehearsal;Username=unused;Password=unused'
            $env:Database__ApplyMigrationsOnStartup = 'false'
            $env:NotificationsDispatcher__Enabled = 'false'
            Push-Location $repo
            try {
                & dotnet build src/FCG.Notifications.Api/FCG.Notifications.Api.csproj -m:1 -p:UseSharedCompilation=false
                if ($LASTEXITCODE -ne 0) { throw 'Build local falhou.' }
                & dotnet ef migrations script --idempotent --no-build --project src/FCG.Notifications.Infrastructure --startup-project src/FCG.Notifications.Api --output $sqlPath
                if ($LASTEXITCODE -ne 0) { throw 'Geracao de migrations falhou.' }
            } finally { Pop-Location }
        } finally {
            $env:ConnectionStrings__NotificationsDatabase = $previousConnection
            $env:Database__ApplyMigrationsOnStartup = $previousMigrations
            $env:NotificationsDispatcher__Enabled = $previousDispatcher
        }
        Assert-Isolation
        $sql = Get-Content -LiteralPath $sqlPath -Raw
        if ($sql -notmatch '20261023155922_SmtpDeliveryEvidence') { throw 'SQL sem migration SMTP.' }
        Invoke-Sql $sql
        Assert-Empty
        Assert-ApiStopped
        Write-Host 'Preparacao concluida. Infraestrutura pronta; API nao iniciada. Etapa 5C requer Start explicito.'
    }
    'Verify' {
        Assert-Isolation
        Invoke-Sql @'
BEGIN TRANSACTION READ ONLY;
SELECT current_database();
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
SELECT count(*) AS notifications FROM notifications;
SELECT count(*) AS inbox FROM inbox_messages;
SELECT column_name FROM information_schema.columns WHERE table_name='notifications' AND column_name IN ('SmtpMessageId','SmtpAcceptance','SentConfirmationToken') ORDER BY column_name;
COMMIT;
'@
        Invoke-Compose exec -T rabbitmq rabbitmqctl list_queues -p /dispatcher-rehearsal name messages_ready messages_unacknowledged consumers
        Invoke-Compose ps --all
    }
    'Start' {
        # Exclusivo para Etapa 5C. Nunca chamado por Build ou Prepare.
        Assert-Isolation
        Assert-ApiStopped
        Assert-Empty
        Invoke-Compose --profile functional up -d --no-build notificationsapi
    }
    'Stop' { Invoke-Compose --profile functional stop }
}
