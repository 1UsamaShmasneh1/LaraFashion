#!/usr/bin/env python3
"""Synthetic policy and real filesystem/crypto tests; no SSH, service or production access."""
import contextlib, copy, hashlib, importlib.util, io, json, os, pathlib, shlex, shutil, stat, subprocess, sys, tempfile
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
# Real filesystem regression: appsettings belongs to the renamed original release, never the new publish.
saved_paths={name:getattr(m,name) for name in ('BASE','LIVE','AUTH','ENV_FILE','DROPIN','MARKER')}
saved_command,saved_run=m.command,m.subprocess.run
try:
    m.BASE=root/'activation/persistent'; m.LIVE=m.BASE/'publish'; m.AUTH=root/'activation/authorization.json'
    m.ENV_FILE=root/'activation/etc/runtime.env'; m.DROPIN=root/'activation/system/service.d/transition.conf'; m.MARKER=m.BASE/'data/maintenance'
    m.LIVE.mkdir(parents=True);m.MARKER.parent.mkdir();(m.BASE/'releases').mkdir();m.DROPIN.parent.parent.mkdir(parents=True)
    old_settings=m.LIVE/'appsettings.json';old_settings.write_text('{"SyntheticOnly":true}')
    (m.LIVE/'LaraFashion.dll').write_bytes(b'synthetic previous application')
    original_files=m.inventory(m.LIVE)
    unit=root/'activation/etc/old.service';unit.parent.mkdir();unit.write_text('[Service]\nUser=synthetic\n')
    def config_row(path):
        s=path.stat();return dict(Path=str(path),Sha256=m.digest(path),Mode=stat.S_IMODE(s.st_mode),Uid=s.st_uid,Gid=s.st_gid)
    config_rows=[config_row(unit),config_row(old_settings)]
    activation_root=m.BASE/'backups/first-transition'/run_id;activation_root.mkdir(parents=True);activation_root.chmod(0o700);os.chown(activation_root,1001,1001)
    m.save(activation_root/'legacy-recovery.json',dict(ApplicationFiles=original_files))
    m.save(activation_root/'original-config-manifest.json',config_rows)
    previous=m.BASE/'releases/previous-synthetic';m.LIVE.rename(previous);m.LIVE.mkdir();(m.LIVE/'LaraFashion.dll').write_bytes(b'synthetic new candidate')
    assert not (m.LIVE/'appsettings.json').exists()
    m.verify_original_configuration(activation_root,previous);passed('renamed-original-config-verified-with-no-appsettings-in-candidate')
    outside=root/'outside-previous';shutil.copytree(previous,outside)
    rejects(lambda:m.verify_original_configuration(activation_root,outside));passed('previous-config-outside-release-root-refused')
    linked=m.BASE/'releases/previous-link';linked.symlink_to(previous,target_is_directory=True)
    rejects(lambda:m.verify_original_configuration(activation_root,linked));passed('symlink-previous-release-refused')
    original=unit.read_bytes();unit.write_bytes(b'changed unit')
    rejects(lambda:m.verify_original_configuration(activation_root,previous));passed('changed-original-unit-refused-after-release-swap');unit.write_bytes(original)
    previous_settings=previous/'appsettings.json';original=previous_settings.read_bytes();previous_settings.write_bytes(b'changed original settings')
    rejects(lambda:m.verify_original_configuration(activation_root,previous));passed('changed-renamed-auth-config-refused');previous_settings.write_bytes(original)
    mode=stat.S_IMODE(previous_settings.stat().st_mode);previous_settings.chmod(0o644 if mode!=0o644 else 0o600)
    rejects(lambda:m.verify_original_configuration(activation_root,previous));passed('changed-original-config-mode-refused');previous_settings.chmod(mode)
    (activation_root/'runtime.env').write_text("ASPNETCORE_ENVIRONMENT='Production'\nSYNTHETIC_SETTING='not-real-secret'\n")
    package_dir=activation_root/'cutover';package_dir.mkdir()
    envelope=package_dir/'synthetic.cms';envelope.write_bytes(b'synthetic envelope for activation-only fixture')
    transfer=dict(RunId=run_id,Files={'synthetic.cms':{'Sha256':m.digest(envelope)}})
    m.save(package_dir/'transfer-manifest.json',transfer)
    m.save(activation_root/'cutover-external-verified.json',dict(RunId=run_id,Package=transfer,DecryptedRestoreVerified=True,AllFilesAndModesVerified=True))
    m.save(m.AUTH,dict(record,State='Claimed',AuthorizedActionRunId='12345',ClaimedActionRunId='12345'))
    def reload_only(args,**kwargs):
        assert args==['systemctl','daemon-reload'];return subprocess.CompletedProcess(args,0)
    m.subprocess.run=reload_only
    m.command=lambda *args: str(m.ENV_FILE)+' (ignore_errors=no)' if args==('systemctl','show','larafashion.service','-p','EnvironmentFiles','--value') else (_ for _ in ()).throw(AssertionError('Unexpected service command'))
    assert invoke('activate',str(previous))['Success']
    assert m.ENV_FILE.read_bytes()==(activation_root/'runtime.env').read_bytes() and stat.S_IMODE(m.ENV_FILE.stat().st_mode)==0o640
    assert m.ENV_FILE.stat().st_uid==0 and m.ENV_FILE.stat().st_gid==1001
    assert m.MARKER.read_text()==run_id+'\n' and m.DROPIN.read_text()=='[Service]\nEnvironmentFile='+str(m.ENV_FILE)+'\n'
    assert not (m.LIVE/'appsettings.json').exists()
    passed('actual-activation-after-rename-keeps-auth-outside-candidate-and-private')
finally:
    m.command,m.subprocess.run=saved_command,saved_run
    for name,value in saved_paths.items():setattr(m,name,value)
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
readiness=script.split('# BEGIN LEGACY READINESS\n',1)[1].split('# END LEGACY READINESS',1)[0]
for name,ready_at,expected_calls,success in [('immediate',1,1,True),('delayed',3,3,True),('unavailable',99,12,False)]:
    count_path=root/('readiness-'+name);count_path.write_text('0')
    command='set -Eeuo pipefail\n'+readiness+'\ntransition() { n=$(cat '+shlex.quote(str(count_path))+'); n=$((n+1)); echo "$n" > '+shlex.quote(str(count_path))+'; [[ "$n" -ge '+str(ready_at)+' ]]; }\nsleep() { [[ "$1" == 5 ]]; }\nwait_for_legacy_health\n'
    result=subprocess.run(['bash','-c',command],capture_output=True,text=True,timeout=10)
    assert (result.returncode==0)==success and int(count_path.read_text())==expected_calls
    passed('bounded-legacy-readiness-'+name+'-without-another-start-or-restore')
assert '! wait_for_legacy_health;' in script and 'transition activate "$ROLLBACK_DIR"' in script
passed('coordinator-uses-typed-readiness-wait-and-explicit-original-release-reference')
print('FIRST_TRANSITION_RESULT: '+str(count)+'/'+str(count)+' passed; synthetic fixtures retained in '+str(root))
