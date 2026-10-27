#!/usr/bin/env python3
"""Synthetic policy and real filesystem/crypto tests; no SSH, service or production access."""
import contextlib, copy, hashlib, importlib.util, io, json, os, pathlib, shutil, subprocess, sys, tempfile
BASE=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('first_transition',BASE/'deploy/first-transition.py')
m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
count=0
def passed(name):
    global count
    count+=1; print('PASS first-transition '+name,flush=True)
def rejects(action):
    try: action()
    except (RuntimeError,ValueError,json.JSONDecodeError,FileNotFoundError): return
    raise AssertionError('Expected transition refusal')
manifest={'OriginalCommit':m.LEGACY_COMMIT,'ApplicationSha256':m.LEGACY_DLL,'SchemaDigest':'synthetic-schema','DatabaseIdentity':'1:2'}
observed={'Pid':123,'ApplicationSha256':m.LEGACY_DLL,'SchemaDigest':'synthetic-schema','DatabaseIdentity':'1:2'}
for key in ('PidStable','ServiceActive','ProcessPathCorrect','ServiceUserCorrect','HomeCorrect','NoIdentityOverrides','ListenerOwned','DatabaseHandleCorrect'): observed[key]=True
m.legacy_validate(manifest,observed,b'{"status":"healthy"}',200); passed('explicit-original-contract-with-independent-proofs')
for key,value in [('ApplicationSha256','other-binary'),('SchemaDigest','other-schema'),('DatabaseIdentity','1:3'),('Pid',0),
                  ('PidStable',False),('ListenerOwned',False),('DatabaseHandleCorrect',False),('ProcessPathCorrect',False),
                  ('ServiceUserCorrect',False),('HomeCorrect',False),('NoIdentityOverrides',False),('ServiceActive',False)]:
    wrong=dict(observed); wrong[key]=value
    rejects(lambda:m.legacy_validate(manifest,wrong,b'{"status":"healthy"}',200)); passed('reject-'+key)
for body,status,name in [(b'{"status":"healthy"}',307,'redirect'),(b'<html>healthy</html>',200,'HTML'),
                         (b'{"status":"healthy","Commit":"manufactured"}',200,'manufactured-health-fields'),(b'{}',200,'empty-health')]:
    rejects(lambda:m.legacy_validate(manifest,observed,body,status)); passed('reject-'+name)
for key in ('OriginalCommit','ApplicationSha256','SchemaDigest','DatabaseIdentity'):
    wrong=dict(manifest); wrong[key]='different'
    rejects(lambda:m.legacy_validate(wrong,observed,b'{"status":"healthy"}',200)); passed('reject-unbound-manifest-'+key)
auth={'AuthorizationMode':m.AUTHORIZATION_MODE,'Host':m.HOST,'HostFingerprint':m.PIN,'State':'Authorized','Closed':False,'Cancelled':False}
m.authorization(auth); passed('explicit-task-lifecycle-authorization-without-fake-date')
for key,value,name in [('AuthorizationMode',None,'missing-mode'),('AuthorizationMode','','blank-mode'),
                     ('AuthorizationMode','2026-10-11T23:38:00Z','legacy-date-is-not-permission'),
                     ('AuthorizedUntilUtc','','missing-date-does-not-grant-permission'),
                     ('AuthorizedUntilUtc','2099-01-01T00:00:00Z','distant-date-not-a-bypass'),
                     ('Host','other-host','different-host'),('HostFingerprint','other-key','different-fingerprint'),
                     ('Closed',True,'closed'),('Cancelled',True,'cancelled'),('State','Closed','closed-state'),
                     ('State','Cancelled','cancelled-state'),('State','','missing-state'),('Closed',None,'missing-closed-marker')]:
    wrong=dict(auth); wrong[key]=value
    rejects(lambda:m.authorization(wrong)); passed('reject-'+name)
root=pathlib.Path(tempfile.mkdtemp(prefix='LaraFashionFirstTransitionSynthetic-')); os.chmod(root,0o700)
assert os.geteuid()==0, 'Run the first-transition filesystem policy fixtures with sudo; they use only this synthetic /tmp root'
saved_auth,saved_base=m.AUTH,m.BASE
m.AUTH=root/'authorization.json'; m.BASE=root/'persistent'
run_id='lf-cutover-synthetic-bound'; commit='1'*40
attempt=m.BASE/'backups/first-transition'/run_id; attempt.mkdir(parents=True); attempt.chmod(0o700); os.chown(attempt,1001,1001)
cert=root/'public-recipient-reference'; cert.write_text('synthetic public reference; no private key')
record=dict(auth,RunId=run_id,CandidateCommit=commit,RecipientCertificate=str(cert),RecipientCertificateSha256=m.digest(cert))
m.save(m.AUTH,record)
assert m.context(run_id,commit,claimed=False)[1]==attempt; passed('actual-root-owned-authorization-and-private-attempt-directory')
for invalid_run,invalid_commit,name in [('lf-cutover-other',commit,'other-attempt'),(run_id,'2'*40,'other-commit')]:
    rejects(lambda:m.context(invalid_run,invalid_commit,claimed=False)); passed('reject-'+name+'-authorization')
for mode in (0o644,0o640):
    m.AUTH.chmod(mode); rejects(lambda:m.context(run_id,commit,claimed=False)); passed('reject-authorization-mode-'+oct(mode))
m.AUTH.chmod(0o600)
def invoke(op,*args):
    previous=sys.argv; sys.argv=['first-transition.py',op,run_id,commit,*args]
    try:
        with contextlib.redirect_stdout(io.StringIO()) as output: m.main()
        return json.loads(output.getvalue())
    finally: sys.argv=previous
os.environ['LARAFASHION_AUTHORIZATION_MODE']=m.AUTHORIZATION_MODE
os.environ['LARAFASHION_ACTION_RUN_ID']='12345'
rejects(lambda:invoke('claim')); passed('unbound-action-cannot-claim')
assert invoke('bind-action','12345')['Success']; passed('real-root-record-binds-action-before-claim')
rejects(lambda:invoke('bind-action','12346')); passed('cannot-rebind-action-or-replay-technical-timeout')
os.environ['LARAFASHION_ACTION_RUN_ID']='12346'; rejects(lambda:invoke('claim')); passed('other-action-refused-before-claim')
os.environ['LARAFASHION_ACTION_RUN_ID']='12345'
assert invoke('claim')['Success'] and json.loads(m.AUTH.read_text())['State']=='Claimed'; passed('claim-bound-to-exact-GitHub-action-run')
rejects(lambda:invoke('claim')); passed('same-action-cannot-claim-twice-or-restart-after-timeout')
assert invoke('check')['Success']; passed('active-claim-rechecked-without-date-command')
os.environ['LARAFASHION_ACTION_RUN_ID']='12346'; rejects(lambda:m.context(run_id,commit)); passed('second-action-cannot-reuse-claimed-authorization')
os.environ['LARAFASHION_ACTION_RUN_ID']='12345'
rejects(lambda:invoke('cancel')); passed('claimed-attempt-cannot-be-cancelled-as-unclaimed')
assert invoke('close')['Success']; passed('actual-close-persists-terminal-authorization')
rejects(lambda:m.context(run_id,commit)); passed('closed-authorization-cannot-replay')
rejects(lambda:invoke('claim')); passed('closed-claim-cannot-be-reopened')
m.save(m.AUTH,record); assert invoke('cancel')['Success']; passed('unclaimed-authorization-cancelled-explicitly')
rejects(lambda:invoke('claim')); passed('cancelled-record-cannot-replay')
m.AUTH.unlink(); rejects(lambda:m.context(run_id,commit,claimed=False)); passed('missing-root-authorization-rejected')
m.AUTH,m.BASE=saved_auth,saved_base
m.save(root/'state.json',{'Stage':'BeforeMutation'}); assert json.loads((root/'state.json').read_text())['Stage']=='BeforeMutation'
assert not (root/'state.json.new').exists(); passed('atomic-fsynced-state-before-mutation')
source=root/'source'; source.mkdir(); (source/'all-uploads-including-unreferenced').write_bytes(b'synthetic upload bytes'); (source/'app.dll').write_bytes(b'synthetic previous binary')
inventory=m.inventory(source); target=root/'copy'; shutil.copytree(source,target)
assert m.inventory(target)==inventory; passed('complete-file-set-mode-owner-and-hash-comparison')
(target/'all-uploads-including-unreferenced').write_bytes(b'changed')
assert m.inventory(target)!=inventory; passed('unreferenced-upload-byte-change-rejected')
(target/'unexpected.key').write_text('synthetic forbidden key')
rejects(lambda:m.inventory(target)); passed('unknown-private-material-rejected')
(target/'unexpected.key').unlink(); (target/'unsafe-link').symlink_to('/tmp/unapproved-outside-file')
rejects(lambda:m.inventory(target)); passed('unsafe-application-link-rejected')
assert shutil.which('openssl'), 'Reviewed OpenSSL CMS is required'
subprocess.run(['openssl','req','-x509','-newkey','rsa:2048','-nodes','-subj','/CN=synthetic transition test',
                '-keyout',str(root/'synthetic.key'),'-out',str(root/'synthetic.cert'),'-days','1'],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
(root/'payload').write_bytes(b'synthetic configuration and DP payload; no real secret')
if os.geteuid()==0: m.encrypt(root/'payload',root/'payload.cms',root/'synthetic.cert')
else: subprocess.run(['openssl','cms','-encrypt','-binary','-aes-256-cbc','-in',str(root/'payload'),'-out',str(root/'payload.cms'),'-outform','DER',str(root/'synthetic.cert')],check=True)
subprocess.run(['openssl','cms','-decrypt','-binary','-inform','DER','-in',str(root/'payload.cms'),'-recip',str(root/'synthetic.cert'),
                '-inkey',str(root/'synthetic.key'),'-out',str(root/'restored')],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
assert m.digest(root/'payload')==m.digest(root/'restored'); passed('trusted-CMS-encryption-isolated-decryption-and-hash-roundtrip')
wf=json.loads((BASE/'.github/workflows/deploy.yml').read_text())
assert wf['jobs']['deploy']['if']=="${{ github.event_name == 'workflow_dispatch' }}"; passed('push-cannot-activate-production')
script=(BASE/'deploy/deploy-production.sh').read_text()
assert script.index('transition verify-receipt cutover')<script.index('maintenance --prepare-legacy-review --plan')
assert script.index('transition check')<script.index('stage WritersStopAttempted')
assert script.rindex('transition check')<script.index('rename_release "$LIVE_DIR" "$ROLLBACK_DIR"')
assert script.index('stage ApplicationStartAttempted')<script.index("start_writers || fail 'Start failed")
passed('external-backup-and-time-and-start-intent-precede-critical-actions')
transport=(BASE/'deploy/actions-ssh.sh').read_text(); remote=(BASE/'deploy/actions-remote.sh').read_text()
assert transport.index('cat -- "$SCRIPT_DIR/actions-preflight.sh"')<transport.index('cat -- "$SCRIPT_DIR/actions-remote.sh"')
assert remote.index('transport-preflight')<remote.index('git fetch')
preflight=(BASE/'deploy/actions-preflight.sh').read_text()
assert 'transport-preflight' in preflight and 'deploy-production.sh' not in preflight and 'systemctl stop' not in preflight and 'git fetch' not in preflight
assert 'date -' not in transport+remote+preflight and 'transition deadline' not in script
passed('actual-Actions-readonly-preflight-before-coordinated-deployment-and-no-date-bypass')
print('FIRST_TRANSITION_RESULT: '+str(count)+'/'+str(count)+' passed; synthetic fixtures retained in '+str(root))
