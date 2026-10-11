#!/usr/bin/env python3
"""Synthetic policy and real filesystem/crypto tests; no SSH, service or production access."""
import copy, datetime as dt, hashlib, importlib.util, json, os, pathlib, shutil, subprocess, tempfile
BASE=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('first_transition',BASE/'deploy/first-transition.py')
m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
count=0
def passed(name):
    global count
    count+=1; print('PASS first-transition '+name,flush=True)
def rejects(action):
    try: action()
    except (RuntimeError,ValueError,json.JSONDecodeError): return
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
auth={'AuthorizedUntilUtc':m.UNTIL}
end=dt.datetime.fromisoformat(m.UNTIL.replace('Z','+00:00'))
assert m.remaining(auth,3600,end-dt.timedelta(seconds=3601))==3601; passed('stop-budget-preserves-thirty-minute-recovery-reserve')
for now,minimum,name in [(end,1,'exact-expiry'),(end+dt.timedelta(seconds=1),0,'expired'),(end-dt.timedelta(seconds=3599),3600,'insufficient-stop-budget')]:
    rejects(lambda:m.remaining(auth,minimum,now)); passed('reject-'+name)
wrong=dict(auth); wrong['AuthorizedUntilUtc']=(end+dt.timedelta(hours=3)).isoformat()
rejects(lambda:m.remaining(wrong,0,end)); passed('reconnect-cannot-extend-fixed-deadline')
root=pathlib.Path(tempfile.mkdtemp(prefix='LaraFashionFirstTransitionSynthetic-')); os.chmod(root,0o700)
assert os.geteuid()==0, 'Run the first-transition filesystem policy fixtures with sudo; they use only this synthetic /tmp root'
saved_auth,saved_base,saved_utc=m.AUTH,m.BASE,m.utc
m.AUTH=root/'authorization.json'; m.BASE=root/'persistent'; m.utc=lambda:end-dt.timedelta(hours=2)
run_id='lf-cutover-synthetic-bound'; commit='1'*40
attempt=m.BASE/'backups/first-transition'/run_id; attempt.mkdir(parents=True); attempt.chmod(0o700); os.chown(attempt,1001,1001)
record={'RunId':run_id,'CandidateCommit':commit,'HostFingerprint':m.PIN,'AuthorizedUntilUtc':m.UNTIL,'Closed':False}
m.save(m.AUTH,record)
assert m.context(run_id,commit,3600,claimed=False)[1]==attempt; passed('actual-root-owned-authorization-and-private-attempt-directory')
for invalid_run,invalid_commit,name in [('lf-cutover-other',commit,'other-attempt'),(run_id,'2'*40,'other-commit')]:
    rejects(lambda:m.context(invalid_run,invalid_commit,0,claimed=False)); passed('reject-'+name+'-authorization')
for mode in (0o644,0o640):
    m.AUTH.chmod(mode); rejects(lambda:m.context(run_id,commit,0,claimed=False)); passed('reject-authorization-mode-'+oct(mode))
m.AUTH.chmod(0o600)
record['ClaimedActionRunId']='12345'; m.save(m.AUTH,record)
os.environ['LARAFASHION_ACTION_RUN_ID']='12345'; assert m.context(run_id,commit,0)[0]['ClaimedActionRunId']=='12345'; passed('claim-bound-to-exact-GitHub-action-run')
os.environ['LARAFASHION_ACTION_RUN_ID']='12346'; rejects(lambda:m.context(run_id,commit,0)); passed('second-action-cannot-reuse-claimed-authorization')
os.environ['LARAFASHION_ACTION_RUN_ID']='12345'; record['Closed']=True; m.save(m.AUTH,record)
rejects(lambda:m.context(run_id,commit,0)); passed('closed-authorization-cannot-replay')
m.AUTH,m.BASE,m.utc=saved_auth,saved_base,saved_utc
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
assert script.index('transition deadline 3600')<script.index('stage WritersStopAttempted')
assert script.index('transition deadline 2100')<script.index('rename_release "$LIVE_DIR" "$ROLLBACK_DIR"')
assert script.index('stage ApplicationStartAttempted')<script.index("start_writers || fail 'Start failed")
passed('external-backup-and-time-and-start-intent-precede-critical-actions')
print('FIRST_TRANSITION_RESULT: '+str(count)+'/'+str(count)+' passed; synthetic fixtures retained in '+str(root))
