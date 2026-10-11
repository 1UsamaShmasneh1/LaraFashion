#!/usr/bin/env python3
"""Real OpenSSH config/reference checks; mocked transport, synthetic credential, no network."""
import json, os, pathlib, shutil, subprocess, tempfile
BASE=pathlib.Path(__file__).resolve().parents[2]
assert os.name=='posix', 'These checks require the Linux OpenSSH client'
ROOT=pathlib.Path(tempfile.mkdtemp(prefix='LaraFashionSshSynthetic-'));ROOT.chmod(0o700)
real_ssh=shutil.which('ssh');assert real_ssh and shutil.which('ssh-keygen')
version=subprocess.run([real_ssh,'-V'],check=True,capture_output=True,text=True)
print('OpenSSH client: '+(version.stderr+version.stdout).strip(),flush=True)
count=0
def passed(name):
 global count
 count+=1;print('PASS ssh-transport '+name,flush=True)
mock="""#!/usr/bin/env bash
set -Eeuo pipefail
[[ "$1" == -F && "$3" == 82.70.208.68 && "$4" == bash && "$5" == -s ]]
[[ -z "${SSH_KEY+x}" ]]
"$REAL_SSH" -G -F "$2" "$3" >"$SSH_FIXTURE/effective-config"
printf '%s\n' "$2" >"$SSH_FIXTURE/config-path"
cp -- "$2" "$SSH_FIXTURE/generated-config"
identity="$(dirname -- "$2")/identity"
[[ "$(stat -c %a -- "$identity")" == 600 ]]
[[ "$(stat -c %a -- "$(dirname -- "$2")")" == 700 ]]
[[ "$(cat "$identity")" == synthetic-credential-unused-no-real-key ]]
cat >"$SSH_FIXTURE/remote-payload"
exit "${SSH_FIXTURE_EXIT:-0}"
"""
def execute(name, changes=None, source=None, success=False, called=False):
 root=ROOT/name;root.mkdir();runner=root/'runner temp with spaces';runner.mkdir()
 mocks=root/'mocks';mocks.mkdir()
 (mocks/'ssh').write_text(mock);(mocks/'ssh').chmod(0o755)
 (mocks/'date').write_text('#!/usr/bin/env bash\nif [[ "$*" == *"-d "* ]]; then echo 7200; else echo "${SSH_FIXTURE_NOW:-0}"; fi\n');(mocks/'date').chmod(0o755)
 env=dict(os.environ,PATH=str(mocks)+os.pathsep+os.environ['PATH'],REAL_SSH=real_ssh,SSH_FIXTURE=str(root),
          RUNNER_TEMP=str(runner),SSH_KEY='synthetic-credential-unused-no-real-key\r\n',
          DEPLOY_COMMIT='1'*40,LARAFASHION_TRANSITION_RUN_ID='lf-cutover-synthetic',LARAFASHION_ACTION_RUN_ID='12345',
          LARAFASHION_FIRST_TRANSITION='1',AUTHORIZED_UNTIL_UTC='2026-10-11T23:38:00Z',
          SSH_HOST_FINGERPRINT='SHA256:PY1p4k7jJ1WXl+Mz2ze1wUFQX9oEfM4YKsBAO6gRKws')
 env.update(changes or {})
 result=subprocess.run(['bash',str(source or BASE/'deploy/actions-ssh.sh')],env=env,capture_output=True,text=True,timeout=15)
 assert (result.returncode==0)==success, name+': '+result.stderr
 assert (root/'remote-payload').exists()==called
 assert not list(runner.iterdir()), 'Ephemeral credentials/config must be removed on success and failure'
 assert 'synthetic-credential-unused' not in result.stdout+result.stderr
 return root
r=execute('success',success=True,called=True)
effective=dict(line.split(' ',1) for line in (r/'effective-config').read_text().splitlines() if ' ' in line)
assert effective['hostkeyalgorithms']=='ssh-ed25519'
for key in ('stricthostkeychecking','batchmode','identitiesonly'):
 assert effective[key] in ('yes','true'),(key,effective[key])
for key in ('updatehostkeys','forwardagent','forwardx11','passwordauthentication','kbdinteractiveauthentication','gssapiauthentication','hostbasedauthentication','controlmaster','permitlocalcommand'):
 if key in effective: assert effective[key] in ('no','false'),(key,effective[key])
assert effective['identityagent']=='none'
# OpenSSH omits a disabled ControlPath from -G instead of printing "none".
assert effective.get('controlpath','none')=='none'
assert '    ControlPath none\n' in (r/'generated-config').read_text()
assert effective['controlpersist'] in ('no','0')
assert effective['globalknownhostsfile']=='/dev/null'
assert effective['preferredauthentications']=='publickey'
assert effective['hostname']=='82.70.208.68' and effective['user']=='ubuntu' and effective['port']=='22'
assert effective['proxycommand']=='none' if 'proxycommand' in effective else True
assert effective['proxyjump']=='none' if 'proxyjump' in effective else True
payload=(r/'remote-payload').read_text()
assert 'export DEPLOY_COMMIT='+('1'*40) in payload and 'export LARAFASHION_ACTION_RUN_ID=12345' in payload
assert 'synthetic-credential-unused' not in payload and 'flock -w 900 9' in payload
passed('real-effective-pinned-ED25519-isolated-config-and-private-success-cleanup')
for name,change in [('wrong-fingerprint',{'SSH_HOST_FINGERPRINT':'SHA256:untrusted'}),
 ('invalid-SHA',{'DEPLOY_COMMIT':'main'}),('invalid-run',{'LARAFASHION_TRANSITION_RUN_ID':'lf-cutover-$(touch injected)'}),
 ('invalid-Action-id',{'LARAFASHION_ACTION_RUN_ID':'../other'}),('missing-key',{'SSH_KEY':''}),
 ('expired-fixed-window',{'SSH_FIXTURE_NOW':'7201'})]:
 execute(name,change);passed(name+'-refused-before-transport')
execute('transport-failure',{'SSH_FIXTURE_EXIT':'23'},called=True);passed('failed-transport-removes-only-ephemeral-credentials')
wrong=ROOT/'wrong-reference';wrong.mkdir()
subprocess.run(['ssh-keygen','-q','-t','ed25519','-N','','-f',str(wrong/'synthetic-host')],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
new_public=(wrong/'synthetic-host.pub').read_text().split()[1]
script=(BASE/'deploy/actions-ssh.sh').read_text().replace('AAAAC3NzaC1lZDI1NTE5AAAAIO0B5HMo3gyvxOC57ahPy/5IU0C5mNYN1SLHrNuzChoS',new_public)
(wrong/'actions-ssh.sh').write_text(script);shutil.copyfile(BASE/'deploy/actions-remote.sh',wrong/'actions-remote.sh')
execute('tampered-reference',source=wrong/'actions-ssh.sh');passed('changed-public-reference-rejected-by-real-fingerprint-before-transport')
wf=json.loads((BASE/'.github/workflows/deploy.yml').read_text());step=wf['jobs']['deploy']['steps'][-1]
assert step['run']=='bash deploy/actions-ssh.sh' and step['env']['SSH_KEY']=='${{ secrets.SSH_KEY }}'
assert wf['jobs']['deploy']['steps'][1]['with']['persist-credentials'] is False
assert 'ssh-keyscan' not in (BASE/'deploy/actions-ssh.sh').read_text()
passed('workflow-exact-transport-source-and-no-keyscan-or-global-host-acceptance')
print('SSH_TRANSPORT_RESULT: '+str(count)+'/'+str(count)+' passed; actual OpenSSH config/keygen, mocked SSH command, no network; fixtures '+str(ROOT))
