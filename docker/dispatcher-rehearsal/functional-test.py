"""Etapa 5C.1: preparation/read-only by default. Publishing is explicit, single shot."""
import argparse
import base64
import datetime as dt
import getpass
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import urllib.request
import uuid
import warnings

ROOT = Path(__file__).resolve().parent
ART_ROOT = ROOT / 'artifacts' / 'functional'
ART = ART_ROOT
PROJECT = 'fcg-notifications-rehearsal'
VHOST = '%2Fdispatcher-rehearsal'
EXCHANGE = 'PurchaseNotificationTestEvent'
QUEUE = 'notifications-purchase-test'
CONSUMER = 'PurchaseNotificationTestEventConsumer'


def scenario_path(name):
    if not re.fullmatch(r'[a-z][a-z0-9-]{0,47}', name or '') or name in {
        'con', 'prn', 'aux', 'nul', *('com' + str(i) for i in range(10)),
        *('lpt' + str(i) for i in range(10))
    }:
        raise RuntimeError('Use a scenario name with 1-48 lowercase letters, digits or hyphens, starting with a letter; no paths.')
    # Compatibility without moving or rewriting any existing evidence.
    if name == 'legacy':
        return ART_ROOT
    parent = ART_ROOT / 'scenarios'
    path = parent / name
    if parent.is_symlink() or path.is_symlink() or path.resolve().parent != parent.resolve():
        raise RuntimeError('Scenario directory must not redirect to another location.')
    return path


def initialize_scenario(name):
    path = scenario_path(name)
    if name == 'legacy' or path.exists():
        raise RuntimeError('Scenario already reserved/existing. Use plan for it or init --scenario with a new name; nothing was overwritten.')
    with warnings.catch_warnings():
        warnings.simplefilter('error', getpass.GetPassWarning)
        try:
            email = getpass.getpass('Controlled recipient (hidden; address only): ')
        except getpass.GetPassWarning:
            raise RuntimeError('Hidden input unavailable; use an interactive terminal.') from None
    event = make_event(email)
    raw = json.dumps(event, indent=2, ensure_ascii=True).encode()
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        path.mkdir()  # Exclusive directory reservation also protects concurrent init.
    except FileExistsError:
        raise RuntimeError('Scenario already exists; nothing was overwritten.') from None
    with (path / 'event.json').open('xb') as handle:
        handle.write(raw)
    with (path / 'event.sha256').open('x') as handle:
        handle.write(hashlib.sha256(raw).hexdigest())
    print('Event saved locally in selected scenario. No network operation. Recipient not displayed.')


def docker(*args, data=None):
    result = subprocess.run(['docker', *args], input=data, capture_output=True, text=True, encoding='utf-8')
    if result.returncode:
        raise RuntimeError('Docker command failed; raw output suppressed to protect sensitive data.')
    return result.stdout


def inspect(service):
    row = json.loads(docker('inspect', f'{PROJECT}-{service}-1'))[0]
    if row['Config']['Labels'].get('com.docker.compose.project') != PROJECT:
        raise RuntimeError('Unexpected Docker project.')
    if set(row['NetworkSettings']['Networks']) != {PROJECT + '_rehearsal'}:
        raise RuntimeError('Unexpected Docker network.')
    return row


def isolation():
    for service, port, host_port in [('postgres', '5432/tcp', '55442'),
                                     ('rabbitmq', '15672/tcp', '15678'),
                                     ('smtp-dev', '8025/tcp', '8028')]:
        row = inspect(service)
        if not row['State']['Running']:
            raise RuntimeError('Rehearsal infrastructure is stopped.')
        if row['NetworkSettings']['Ports'][port] != [{'HostIp': '127.0.0.1', 'HostPort': host_port}]:
            raise RuntimeError('Unexpected local port.')
        env = dict(v.split('=', 1) for v in row['Config']['Env'] if '=' in v)
        if service == 'postgres' and env.get('POSTGRES_DB') != 'fcg_notifications_rehearsal':
            raise RuntimeError('Unexpected database.')
        if service == 'rabbitmq' and env.get('RABBITMQ_DEFAULT_VHOST') != '/dispatcher-rehearsal':
            raise RuntimeError('Unexpected vhost.')
        for mount in row['Mounts']:
            if mount['Type'] == 'volume' and mount['Name'] not in {
                PROJECT + '_postgres-data', PROJECT + '_rabbitmq-data'
            }:
                raise RuntimeError('Unexpected volume.')


def http(path, body=None, mailhog=False):
    # No redirects: credentials must never be forwarded to another host.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            return None
    headers = {'Content-Type': 'application/json'}
    if not mailhog:
        row = inspect('rabbitmq')
        env = dict(v.split('=', 1) for v in row['Config']['Env'] if '=' in v)
        credential = env['RABBITMQ_DEFAULT_USER'] + ':' + env['RABBITMQ_DEFAULT_PASS']
        headers['Authorization'] = 'Basic ' + base64.b64encode(credential.encode()).decode()
    url = ('http://127.0.0.1:8028' if mailhog else 'http://127.0.0.1:15678') + path
    request = urllib.request.Request(url, data=body, headers=headers)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(request, timeout=15) as response:
        return json.load(response)


def make_event(email):
    email = email.strip()
    if not re.fullmatch(r'[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+', email):
        raise RuntimeError('Use only one recipient address, without display name, brackets or internal spaces; value omitted.')
    return {**{k: str(uuid.uuid4()) for k in ['EventId', 'CorrelationId', 'UserId', 'OrderId', 'GameId']},
            'OccurredAt': dt.datetime.now(dt.timezone.utc).isoformat(),
            'Version': 1, 'Amount': 19.90, 'Currency': 'BRL', 'Email': email}


def load_event():
    if not (ART / 'event.json').is_file() or not (ART / 'event.sha256').is_file():
        raise RuntimeError('Selected scenario has no complete event/hash. Check --scenario; do not replace previous evidence.')
    raw = (ART / 'event.json').read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    if digest != (ART / 'event.sha256').read_text().strip():
        raise RuntimeError('Event changed; do not regenerate IDs to retry.')
    event = json.loads(raw)
    for key in ['EventId', 'CorrelationId', 'UserId', 'OrderId', 'GameId']:
        if uuid.UUID(event[key]).int == 0:
            raise RuntimeError('Empty identifier.')
    return raw, event, digest


def database(event):
    # Canonical UUID before interpolation; never interpolate email or arbitrary strings.
    event_id = str(uuid.UUID(event['EventId']))
    sql = f'''BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT json_build_object(
 'database', current_database(),
 'inbox', (SELECT coalesce(json_agg(i), '[]') FROM
   (SELECT "EventId", "ConsumerName", "ProcessedAt" FROM inbox_messages
    WHERE "EventId"='{event_id}' AND "ConsumerName"='{CONSUMER}') i),
 'deliveries', (SELECT coalesce(json_agg(n), '[]') FROM
   (SELECT "Id", "SmtpDestinationId", "Status", "AttemptCount", "SmtpMessageId",
           "SmtpAcceptance", "SentConfirmationToken", "SentAt", "UpdatedAt", "FailureCode"
    FROM notifications WHERE "EventId"='{event_id}' ORDER BY "SmtpDestinationId") n));
COMMIT;'''
    output = docker('exec', '-i', PROJECT + '-postgres-1', 'psql', '-X', '-qAt',
                    '-v', 'ON_ERROR_STOP=1', '-U', 'notifications_rehearsal',
                    '-d', 'fcg_notifications_rehearsal', data=sql)
    result = json.loads(output)
    if result['database'] != 'fcg_notifications_rehearsal':
        raise RuntimeError('Unexpected database.')
    return result


def mailhog(db):
    ids = {n['SmtpMessageId'] for n in db['deliveries'] if n['SmtpDestinationId'] == 'mailhog'}
    matches = []
    start = 0
    while True:
        page = http(f'/api/v2/messages?start={start}&limit=100', mailhog=True)
        items = page.get('items', [])
        for item in items:
            headers = item.get('Content', {}).get('Headers', {})
            message_ids = next((v for k, v in headers.items() if k.lower() == 'message-id'), [])
            if any(value.strip().strip('<>') in ids for value in message_ids):
                matches.append(item['ID'])
        start += len(items)
        if not items or start >= page['total']:
            break
    return {'matching_count': len(matches), 'mailhog_ids': sorted(matches)}


def queue_stats():
    rows = http(f'/api/queues/{VHOST}')
    return [{k: row.get(k, 0) for k in ['name', 'messages_ready', 'messages_unacknowledged', 'consumers']}
            for row in rows if row['name'].startswith(QUEUE)]


def snapshot(event):
    db = database(event)
    return {'db': db, 'mailhog': mailhog(db)}


def require_sent(state):
    rows = state['db']['deliveries']
    if len(state['db']['inbox']) != 1 or len(rows) != 2 or {r['SmtpDestinationId'] for r in rows} != {'gmail', 'mailhog'}:
        raise RuntimeError('Expected one Inbox record and two independent destinations.')
    if any(r['Status'] != 'Sent' or r['AttemptCount'] != 1 or not r['SmtpAcceptance']
           or not r['SentConfirmationToken'] or not r['SentAt'] for r in rows):
        raise RuntimeError('Baseline requires both Sent, one attempt and acceptance evidence.')
    if len({r['SmtpMessageId'] for r in rows}) != 2 or state['mailhog']['matching_count'] != 1:
        raise RuntimeError('Unexpected Message-Ids or MailHog count.')


def publish(raw, event, digest, repeat, confirmed):
    if not confirmed:
        raise RuntimeError('Publishing requires --confirm-send, exclusively in Etapa 5C.2.')
    api = inspect('notificationsapi')
    env = dict(v.split('=', 1) for v in api['Config']['Env'] if '=' in v)
    if not api['State']['Running'] or env.get('NotificationsDispatcher__Enabled') != 'true':
        raise RuntimeError('Persistent API is not running; this tool never starts it.')
    if env.get('RabbitMq__VirtualHost') != '/dispatcher-rehearsal' or env.get('RabbitMq__Host') != 'rabbitmq':
        raise RuntimeError('Unexpected API broker.')
    queue = http(f'/api/queues/{VHOST}/{QUEUE}')
    if queue.get('consumers') != 1:
        raise RuntimeError('Expected one consumer; do not publish before topology is ready.')
    http(f'/api/exchanges/{VHOST}/{EXCHANGE}')  # Require topology; never declare it here.
    if repeat:
        receipt = json.loads((ART / 'publish.receipt.json').read_text())
        if receipt != {'sha256': digest, 'routed': True}:
            raise RuntimeError('Initial publication not confirmed.')
        baseline = json.loads((ART / 'baseline.json').read_text())
        require_sent(baseline)
        if snapshot(event) != baseline:
            raise RuntimeError('State changed since baseline; investigate before repeat.')
    elif database(event)['inbox'] or database(event)['deliveries']:
        raise RuntimeError('Event already persisted; use repeat only after baseline.')
    name = 'repeat' if repeat else 'publish'
    # Write intent before HTTP: lost response must not silently trigger a second POST.
    with (ART / f'{name}.intent.json').open('x') as handle:
        json.dump({'sha256': digest, 'at': dt.datetime.now(dt.timezone.utc).isoformat()}, handle)
    body = json.dumps({'properties': {'content_type': 'application/json', 'delivery_mode': 2,
                                     'message_id': event['EventId']},
                       'routing_key': '', 'payload': raw.decode('utf-8'),
                       'payload_encoding': 'string'}).encode()
    result = http(f'/api/exchanges/{VHOST}/{EXCHANGE}/publish', body=body)
    routed = result.get('routed') is True
    (ART / f'{name}.receipt.json').write_text(json.dumps({'sha256': digest, 'routed': routed}))
    if not routed:
        raise RuntimeError('Not routed. Intent retained; investigate, do not retry blindly.')
    print(json.dumps({'event_id': event['EventId'], 'sha256': digest, 'routed': True}))


def main():
    global ART
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['init', 'plan', 'isolation', 'db', 'mailhog', 'queues', 'logs',
                                         'snapshot', 'compare', 'publish', 'repeat'])
    parser.add_argument('--confirm-send', action='store_true')
    parser.add_argument('--scenario', help='Explicit scenario name; legacy selects the original files without migration.')
    args = parser.parse_args()
    if args.action != 'isolation':
        if args.scenario is None:
            parser.error('--scenario is required (use legacy for the original event).')
        ART = scenario_path(args.scenario)
    if args.action == 'init':
        initialize_scenario(args.scenario)
        return
    if args.action == 'isolation':
        isolation()
        print(json.dumps({'project': PROJECT, 'database': 'fcg_notifications_rehearsal',
                          'vhost': '/dispatcher-rehearsal', 'mailhog_port': 8028,
                          'queues': queue_stats(),
                          'mailhog_total': http('/api/v2/messages?limit=1', mailhog=True)['total']}))
        return
    raw, event, digest = load_event()
    if args.action == 'plan':
        print(json.dumps({'scenario': args.scenario, 'event_id': event['EventId'], 'sha256': digest, 'exchange': EXCHANGE,
                          'vhost': '/dispatcher-rehearsal', 'content_type': 'application/json'}))
        return
    # Prevent even network reads on an unconfirmed publication command.
    if args.action in ['publish', 'repeat'] and not args.confirm_send:
        raise RuntimeError('Use --confirm-send only in Etapa 5C.2. No network operation performed.')
    isolation()
    if args.action == 'db':
        print(json.dumps(database(event), indent=2))
    elif args.action == 'mailhog':
        print(json.dumps(mailhog(database(event)), indent=2))
    elif args.action == 'queues':
        print(json.dumps(queue_stats(), indent=2))
    elif args.action == 'snapshot':
        state = snapshot(event)
        require_sent(state)
        with (ART / 'baseline.json').open('x') as handle:
            json.dump(state, handle, sort_keys=True)
        print('Baseline saved. Verify Gmail manually before repeat.')
    elif args.action == 'compare':
        state = snapshot(event)
        require_sent(state)
        if state != json.loads((ART / 'baseline.json').read_text()):
            raise RuntimeError('Database/MailHog changed since baseline.')
        print('Database and MailHog unchanged. This alone does not prove replay consumption or Gmail absence of duplicates.')
    elif args.action == 'logs':
        inspect('notificationsapi')
        result = subprocess.run(['docker', 'logs', '--since', event['OccurredAt'],
                                 PROJECT + '-notificationsapi-1'], capture_output=True, text=True, encoding='utf-8')
        if result.returncode:
            raise RuntimeError('API logs unavailable; no raw output displayed.')
        text = result.stdout + result.stderr
        ids = {r['Id'] for r in database(event)['deliveries']}
        entries = []
        for match in re.finditer(r'Entrega ([0-9a-f-]{36}): estado ([A-Za-z]+), etapa ([A-Z_]+),', text):
            if match[1] in ids:
                entries.append({'notification_id': match[1], 'status': match[2], 'stage': match[3]})
        print(json.dumps({'dispatcher': entries, 'consumer_processed_log_count': text.count(
            'Evento de teste processado no modo persistente. EventId=' + event['EventId'])}, indent=2))
    else:
        publish(raw, event, digest, args.action == 'repeat', args.confirm_send)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # No HTTP body, SMTP recipient, credentials, or arbitrary exception text.
        print('FAILED: ' + (str(error) if isinstance(error, RuntimeError) else type(error).__name__), file=sys.stderr)
        sys.exit(1)
