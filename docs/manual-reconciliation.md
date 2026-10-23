# Reconciliação interna de notificações

Não há endpoints HTTP nem autorização de usuário nesta camada. Um futuro adaptador administrativo deve autenticar o operador, autorizar a ação e obter OperatorId de uma identidade confiável, nunca confiar apenas em parâmetros externos.

Use INotificationOperationsQuery.GetAsync para obter Status e UpdatedAt. Cada decisão tem ActionId estável: retries devem reutilizar exatamente a solicitação anterior. Alterar qualquer parâmetro exige nova decisão e novo ActionId. Uma resposta de commit perdida pode ser resolvida repetindo a mesma solicitação, sem repetir SMTP. O resultado repetido representa a decisão original, não necessariamente o estado atual da entrega.

INotificationReconciliation.ReconcileAsync confirma Unknown como Sent mediante evidência externa, ou reagenda Failed como Pending após investigação. SentAt na confirmação manual é a data da reconciliação, não uma alegação sobre a hora real de aceitação SMTP. O histórico distingue a confirmação manual pelo ActionType e conserva o código/timestamps da falha anterior na entrega. Tentativas não são zeradas. Pending fica imediatamente elegível quando o dispatcher estiver ativo.

## Reenvio de Unknown: bloqueado por padrão

Não existe implementação de produção de INotificationReplaySafety nesta etapa. Portanto, Unknown -> Pending é bloqueado mesmo com confirmação do risco e evidência informada. Os testes usam um guardião controlado somente em ambiente sem dispatchers reais.

Para habilitar essa operação futuramente, uma integração interna confiável precisa:

1. Impedir reinício automático, escalonamento e novas instâncias de TODOS os dispatchers relevantes.
2. Encerrar e verificar o término de todos os processos antigos. Desabilitar a opção, expirar a reserva, aguardar timeout ou observar Unknown não comprova término de SMTP.
3. Registrar evidência verificável dessa parada e da investigação no provedor. O provedor ainda pode entregar uma mensagem já aceita; a parada não elimina o risco de duplicidade.
4. Conceder uma lease de quiescência, mantendo a barreira de reinício até o término da transação (incluindo rollback em falhas).
5. Exigir reconhecimento explícito do risco de duplicidade. Só retomar os dispatchers após a conclusão da decisão.

Não implementar o guardião como um simples booleano, configuração Enabled=false ou declaração do solicitante. Não existe caminho alternativo por SQL manual nesta API.

## Auditoria

Uma linha append-only por ActionId guarda operador, justificativa, referências, estados e hash dos parâmetros. UPDATE, DELETE e TRUNCATE são bloqueados por triggers PostgreSQL. O proprietário/superusuário do banco pode remover essas proteções: a aplicação em produção não deve possuir privilégios administrativos de DDL.

Reason deve ser um resumo operacional sem dados pessoais; EvidenceReference e QuiescenceEvidenceReference devem ser identificadores opacos de tickets/evidências, nunca credenciais, URLs assinadas, corpos de e-mail ou respostas SMTP completas. A validação limita tamanho e caracteres de controle, mas não identifica segredos arbitrários digitados por operadores.

A migration adiciona somente a tabela de auditoria, índices, FK e triggers. Nenhuma migration é aplicada ao ambiente integrado por esta implementação. Falhas de validação/conflitos não registram uma decisão concluída; somente transições efetivadas têm auditoria.
