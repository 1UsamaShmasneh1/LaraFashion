#!/usr/bin/env python3
"""Offline Linux acceptance. Synthetic files only; no production/default deploy invocation."""
import fcntl, hashlib, importlib.util, json, os, pathlib, select, shlex, shutil
import signal, sqlite3, subprocess, sys, tempfile, time, urllib.request

BASE = pathlib.Path(sys.argv[1]).resolve()
HEAD = sys.argv[2]
EVIDENCE = pathlib.Path('/evidence')
EVIDENCE.mkdir(mode=0o700)
os.umask(0o077)
metadata = json.loads(pathlib.Path('/input-manifest.json').read_text(encoding='utf-8-sig'))
TEMP = pathlib.Path('/tmp') / metadata['RunId']
TEMP.mkdir(mode=0o711)
TEMP.chmod(0o711)
os.environ.update(TMPDIR=str(TEMP), DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
                  DOTNET_GCHeapHardLimit='0x20000000',MSBUILDDISABLENODEREUSE='1',DOTNET_CLI_USE_MSBUILD_SERVER='0')
tempfile.tempdir = str(TEMP)
APP = TEMP / 'app-drop'
DROP = TEMP / 'tool-drop'
TOOL = DROP / 'LaraFashion.IntegrationTests.dll'
RESULTS = []

def phase(name):
    (EVIDENCE/'progress.json').write_text(json.dumps({'Phase':name,'At':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())}))
    print('PHASE '+name,flush=True)

def passed(name, **details):
    RESULTS.append(dict(Name=name, Status='PASS', **details))
    print('PASS linux '+name,flush=True)

def run(arguments, log=None, timeout=180, env=None, cwd=BASE, check=True, input=None):
    result = subprocess.run(list(map(str,arguments)),input=input,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,
                            text=True,timeout=timeout,env=env,cwd=cwd)
    if log: (EVIDENCE/log).write_text(result.stdout)
    if check and result.returncode: raise AssertionError('Command failed: '+str(arguments[0])+'; log='+str(log)+'; exit='+str(result.returncode))
    return result

def tool(root, operation, *args, **kw):
    return run(['dotnet',TOOL,'--test-linux-acceptance',root,operation,*args],**kw)

def fixture(name):
    root=TEMP/name; root.mkdir(mode=0o700)
    (root/'.larafashion-linux-acceptance').write_text('LaraFashion synthetic Linux acceptance v1')
    return root

def line(process, timeout=30):
    buffer=getattr(process,'_linux_output_buffer',b'')
    deadline=time.monotonic()+timeout
    while b'\n' not in buffer:
        assert select.select([process.stdout],[],[],max(0,deadline-time.monotonic()))[0], 'Process handshake timed out'
        data=os.read(process.stdout.fileno(),4096); assert data, 'Process ended before handshake'
        buffer+=data
    value,buffer=buffer.split(b'\n',1)
    process._linux_output_buffer=buffer
    value=value.decode().strip()
    assert value, 'Process ended before handshake'
    return value

def send(process, value):
    process.stdin.write(value+'\n'); process.stdin.flush()

def probe_lock(path, expected):
    result=run(['/usr/bin/flock','-n','-E','73',path,'true'],check=False)
    assert result.returncode==expected, 'Unexpected independent-open flock result'

def lock_tests():
    phase('real-FD9-flock')
    root=fixture('locks'); source=(BASE/'deploy/deploy-production.sh').read_text()
    fragment=source[source.index('# BEGIN LOCK GUARD FUNCTIONS'):source.index('PROJECT_FILE=')]
    functions=fragment[:fragment.index('# END LOCK GUARD FUNCTIONS')+len('# END LOCK GUARD FUNCTIONS')]
    workflow=json.loads((BASE/'.github/workflows/deploy.yml').read_text())
    wrapper=(BASE/'deploy/actions-remote.sh').read_text()
    assert functions==wrapper[wrapper.index('# BEGIN LOCK GUARD FUNCTIONS'):wrapper.index('# END LOCK GUARD FUNCTIONS')+len('# END LOCK GUARD FUNCTIONS')]
    section=root/'lock-section.sh'
    section.write_text('set -Eeuo pipefail\nlog() { printf "%s\\n" "$*" >&2; }\n'+fragment)
    lock=root/'deployment.lock'
    prefix='umask 077; export LOCK_FILE='+shlex.quote(str(lock))+'; '
    holder=subprocess.Popen(['bash','-c',prefix+'exec 9<>"$LOCK_FILE"; flock 9; echo READY; read -r go; '
        +'LARAFASHION_DEPLOY_LOCK_HELD=1 bash '+shlex.quote(str(section))+'; echo CHILD_ACCEPTED; read -r release; exec 9>&-; echo CLOSED; read -r end'],
        stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    try:
        assert line(holder)=='READY'; probe_lock(lock,73)
        send(holder,'GO'); assert line(holder)=='CHILD_ACCEPTED'; probe_lock(lock,73)
        send(holder,'RELEASE'); assert line(holder)=='CLOSED'; probe_lock(lock,0)
        send(holder,'END'); assert holder.wait(timeout=10)==0
    finally:
        if holder.poll() is None: holder.kill(); holder.wait()
    passed('wrapper-to-child-real-FD9-no-self-deadlock-and-independent-conflict')
    for name, descriptor in [('flag-without-FD',''),('closed-FD9','exec 9<>"$LOCK_FILE"; flock 9; exec 9>&-; '),
                             ('wrong-file-FD9','exec 9<>'+shlex.quote(str(root/'other.lock'))+'; flock 9; '),
                             ('same-file-unlocked-FD9','exec 9<>"$LOCK_FILE"; ')]:
        result=run(['bash','-c',prefix+descriptor+'export LARAFASHION_DEPLOY_LOCK_HELD=1; bash '+shlex.quote(str(section))],check=False)
        assert result.returncode!=0 and ('descriptor' in result.stdout.lower() or 'lock not held' in result.stdout.lower())
        passed('reject-'+name)
    # Child retains the inherited open description after wrapper exit, then releases on death.
    fifo=root/'release.fifo'; os.mkfifo(fifo,0o600)
    child=root/'holder-child.sh'; child.write_text('echo CHILD_READY; read -r release < '+shlex.quote(str(fifo))+'\n')
    parent=subprocess.Popen(['bash','-c',prefix+'exec 9<>"$LOCK_FILE"; flock 9; bash '+shlex.quote(str(child))+' & echo CHILD_PID:$!; wait'],stdout=subprocess.PIPE,text=True,start_new_session=True)
    try:
        values=[line(parent),line(parent)]; pid=int(next(v.split(':')[1] for v in values if v.startswith('CHILD_PID:')))
        assert 'CHILD_READY' in values
        os.kill(parent.pid,signal.SIGKILL); parent.wait(timeout=10); probe_lock(lock,73)
        assert str(child).encode() in pathlib.Path(f'/proc/{pid}/cmdline').read_bytes()
        os.kill(pid,signal.SIGTERM)
        deadline=time.monotonic()+10
        while run(['/usr/bin/flock','-n','-E','73',lock,'true'],check=False).returncode==73:
            assert time.monotonic()<deadline; time.sleep(.01)
        probe_lock(lock,0)
    finally:
        try: os.killpg(parent.pid,signal.SIGKILL)
        except ProcessLookupError: pass
    passed('descriptor-lifetime-wrapper-death-child-retains-and-final-close-releases')
    # Execute the exact production 900-second timeout concurrently with the remaining suite.
    timeout_fd=open(lock,'r+'); fcntl.flock(timeout_fd,fcntl.LOCK_EX)
    timeout_log=open(EVIDENCE/'lock-timeout.log','w')
    timeout_process=subprocess.Popen(['bash','-c',prefix+'bash '+shlex.quote(str(section))],stdout=timeout_log,stderr=subprocess.STDOUT)
    return timeout_process,timeout_fd,timeout_log,time.monotonic()

def identity_state_tests():
    phase('real-file-identity-state')
    root=fixture('identity-state'); original=root/'original.bin'; original.write_bytes(b'synthetic identical content')
    (root/'symlink.bin').symlink_to(original); os.link(original,root/'hardlink.bin'); shutil.copyfile(original,root/'copy.bin')
    result=tool(root,'identity','original.bin','symlink.bin','hardlink.bin','copy.bin')
    ids=json.loads(result.stdout)
    expected=hashlib.sha256(('linux:'+str(original.stat().st_dev)+':'+str(original.stat().st_ino)).encode()).hexdigest().upper()
    assert ids[0]==ids[1]==ids[2]==expected and ids[3]!=ids[0]
    passed('actual-application-fstat-device-inode-symlink-hardlink-distinct-identical-copy')
    writer=subprocess.Popen(['strace','-f','-yy','-e','trace=fsync,fdatasync,rename,renameat,renameat2,openat','-o',str(EVIDENCE/'state-syscalls.log'),
        'dotnet',str(TOOL),'--test-linux-acceptance',str(root),'state-save','state.json'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    reader_code='''import json,pathlib,sys,time
r=pathlib.Path(sys.argv[1]); reads=0; previous=None
while not (r/'reader-stop').exists():
    d=json.loads((r/'state.json').read_text()); assert d['ContractVersion']==1 and len(d['Stages'])==8 and all(v=='x'*4096 for v in d['Stages']); reads+=1
    if d['Stage']!=previous:
        previous=d['Stage']; print('OBSERVED '+previous,flush=True)
print('READS '+str(reads),flush=True)
'''
    try:
        assert line(writer)=='SAVED 0'
        reader=subprocess.Popen(['python3','-c',reader_code,str(root)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        try:
            assert line(reader)=='OBSERVED 0'
            for i in range(1,25):
                send(writer,'NEXT'); assert line(writer)=='SAVED '+str(i)
                assert line(reader)=='OBSERVED '+str(i)
            # Kill the owned .NET writer, rather than strace, after its flushed Save acknowledgement.
            children=pathlib.Path(f'/proc/{writer.pid}/task/{writer.pid}/children').read_text().split()
            assert len(children)==1
            pid=int(children[0]); assert b'--test-linux-acceptance' in pathlib.Path(f'/proc/{pid}/cmdline').read_bytes()
            os.kill(pid,signal.SIGKILL); writer.wait(timeout=10)
            assert tool(root,'state-read','state.json').stdout.strip()=='READ 24'
        finally:
            (root/'reader-stop').write_text('stop'); output,error=reader.communicate(timeout=10)
            assert reader.returncode==0 and output.startswith('READS ') and int(output.split()[1])>=25, error
    finally:
        if writer.poll() is None: writer.kill(); writer.wait()
    trace=(EVIDENCE/'state-syscalls.log').read_text()
    lines=trace.splitlines()
    file_sync=next(i for i,l in enumerate(lines) if 'fsync(' in l and str(root) in l and '.tmp' in l and '= 0' in l)
    rename=next(i for i,l in enumerate(lines) if 'rename' in l and 'state.json' in l and '= 0' in l)
    directory_sync=next(i for i,l in enumerate(lines) if 'fsync(' in l and str(root) in l and '.tmp' not in l and '= 0' in l)
    assert file_sync<rename<directory_sync
    passed('atomic-replacement-real-file-and-directory-fsync-independent-reader-after-SIGKILL',ConcurrentReaderValidated=True)
    # Actual Unix access failure after file flush/rename: write+traverse allowed, directory open-for-read denied.
    denied=root/'no-directory-read'; denied.mkdir(mode=0o300); os.chown(denied,1654,1654); root.chmod(0o711)
    (root/'.larafashion-linux-acceptance').chmod(0o644)
    result=run(['/usr/sbin/runuser','-u','app','--','dotnet',TOOL,'--test-linux-acceptance',root,'state-save','no-directory-read/state.json'],check=False,input='',log='permission-sync-denial.log')
    assert result.returncode!=0 and 'IOException' in result.stdout and (denied/'state.json').exists(), 'Directory-sync denial did not reach Save; see permission-sync-denial.log'
    passed('actual-directory-sync-open-denial-propagates-after-atomic-replacement')
    # Fault-injection is limited to the negative syscall test; the preceding success trace is unmodified.
    fault=run(['strace','-f','-e','inject=fsync:error=EIO:when=2','-o',EVIDENCE/'fsync-failure.log',
        'dotnet',TOOL,'--test-linux-acceptance',root,'state-save','fault-state.json'],check=False,input='')
    assert fault.returncode!=0 and 'IOException' in fault.stdout and 'EIO' in (EVIDENCE/'fsync-failure.log').read_text()
    passed('fsync-EIO-error-propagates',NegativeCase='strace errno injection only; success fsync/fstat are real')

def published_app_tests():
    phase('actual-published-production-HTTP-and-DP-restarts')
    service_temp=TEMP/'service-temp'; service_temp.mkdir(mode=0o700); os.chown(service_temp,1654,1654)
    root=fixture('service-temp/web'); tool(root,'init')
    # The actual HTTP release has its own manifest. Harness candidates start from the untouched publish bundle.
    release=TEMP/'http-app'; shutil.copytree(APP,release)
    run(['dotnet',TOOL,'--deployment-manifest',release,TEMP/'http-manifest-result.json'])
    (root/'keys').mkdir(mode=0o700); (root/'uploads').mkdir(mode=0o700)
    for directory in (release,DROP):
        for p in [directory,*directory.rglob('*')]: p.chmod(0o755 if p.is_dir() else 0o644)
    for p in [root,*root.rglob('*')]: os.chown(p,1654,1654)
    env=os.environ.copy()
    env.update(TMPDIR=str(service_temp),ASPNETCORE_ENVIRONMENT='Production', ASPNETCORE_URLS='http://127.0.0.1:59993',ASPNETCORE_HTTPS_PORT='443',
        LARAFASHION_DATABASE_PATH=str(root/'test.db'),LARAFASHION_UPLOADS_PATH=str(root/'uploads'),
        LARAFASHION_DATA_PROTECTION_PATH=str(root/'keys'),JwtSettings__SecretKey='linux-synthetic-only-key-012345678901234567890',
        JwtSettings__Issuer='linux-synthetic',JwtSettings__Audience='linux-synthetic',JwtSettings__ExpiryMinutes='60')
    opener=urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    old_keys=None
    for stage in ('preview','confirm','replay'):
        tool(root,'snapshot','before-'+stage+'.json')
        os.chown(root/('before-'+stage+'.json'),1654,1654)
        child_env=env.copy(); child_env.update(DOTNET_STARTUP_HOOKS=str(TOOL),LARAFASHION_LINUX_TEST_ROOT=str(root),LARAFASHION_LINUX_TEST_PHASE=stage)
        log=open(EVIDENCE/('app-'+stage+'.log'),'w')
        process=subprocess.Popen(['/usr/sbin/runuser','-u','app','--','dotnet',str(release/'LaraFashion.dll')],cwd=release,env=child_env,
            stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
        try:
            probe=root/('probe-'+stage+'.json'); deadline=time.monotonic()+45
            while not probe.exists():
                assert process.poll() is None and time.monotonic()<deadline, 'Published host/probe did not start: '+stage
                time.sleep(.02)
            details=json.loads(probe.read_text()); assert details['Success'], 'Actual host DP probe failed: '+str(details)
            assert details['Replayed']==(stage=='replay')
            if stage=='preview': tool(root,'compare','before-preview.json')
            tool(root,'snapshot','after-start-'+stage+'.json')
            response=opener.open('http://127.0.0.1:59993/health',timeout=5)
            body=response.read().decode(); assert response.status==200 and response.geturl()=='http://127.0.0.1:59993/health'
            health=json.loads(body); pid=details['ProcessId']
            # Default Docker capabilities restrict cross-UID /proc FD inspection, even for root.
            # Inspect the actual process as its own service UID; no extra capability or mock.
            owners=run(['/usr/sbin/runuser','-u','app','--','/usr/bin/fuser','-n','tcp','59993']).stdout
            sockets=run(['/usr/sbin/runuser','-u','app','--','ss','-ltnp','sport = :59993']).stdout
            assert '127.0.0.1:59993' in sockets and 'pid='+str(pid)+',' in sockets and str(pid) in owners
            assert pathlib.Path(f'/proc/{pid}/status').read_text().split('Uid:')[1].strip().split()[0]=='1654'
            assert health['commit']==HEAD and health['migration']=='20261005191438_AddHistoricalOrderInventoryReview' and health['processId']==pid
            identity=json.loads(tool(root,'identity','test.db').stdout)[0]; assert health['databaseIdentity']==identity
            assert health['contractVersion']==1 and health['status']=='healthy'
            health_path=root/('health-'+stage+'.json'); health_path.write_text(body)
            result=root/('health-result-'+stage+'.json')
            run(['dotnet',TOOL,'--deployment-health',root/'test.db',health_path,HEAD,pid,result])
            run(['dotnet',TOOL,'--deployment-check-result',result,'--deployment-health',HEAD])
            tool(root,'compare','after-start-'+stage+'.json')
            keys={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in (root/'keys').glob('*.xml')}
            assert keys and (old_keys is None or keys==old_keys); old_keys=keys
            assert details['KeyRepository']==str(root/'keys') and details['Discriminator']==str(release)+'/' and details['StartupBusinessPreserved']
            for private in (root/'keys',root/'request.json',root/'test.db'):
                assert run(['/usr/sbin/runuser','-u','nobody','--','test','-r',private],check=False).returncode!=0
            passed('published-'+stage+'-real-GET-contract-PID-DB-and-DP',**details)
        finally:
            try: os.killpg(process.pid,signal.SIGTERM)
            except ProcessLookupError: pass
            try: process.wait(timeout=15)
            except subprocess.TimeoutExpired: os.killpg(process.pid,signal.SIGKILL); process.wait(timeout=10)
            log.close()
            assert not run(['ss','-ltn','sport = :59993']).stdout.strip().splitlines()[1:]
    passed('synthetic-DP-keys-reused-ticket-confirmed-after-restart-and-replay-with-one-movement')
    # Startup failures use explicit synthetic paths and the same real published entry point, no hook.
    incompatible=root/'incompatible.db'
    with sqlite3.connect(incompatible) as db: db.execute('CREATE TABLE Synthetic (Id INTEGER)')
    os.chown(incompatible,1654,1654)
    no_admin=root/'no-admin.db'
    with sqlite3.connect(root/'test.db') as source, sqlite3.connect(no_admin) as destination:
        source.backup(destination); destination.execute('DELETE FROM AdminUsers'); destination.commit()
    os.chown(no_admin,1654,1654)
    for name, change in [('missing-db',{'LARAFASHION_DATABASE_PATH':str(root/'missing.db')}),
                         ('no-existing-admin',{'LARAFASHION_DATABASE_PATH':str(no_admin)}),
                         ('incompatible-schema',{'LARAFASHION_DATABASE_PATH':str(incompatible)}),
                         ('missing-runtime-config',{'JwtSettings__Issuer':None}),
                         ('DP-path-missing',{'LARAFASHION_DATA_PROTECTION_PATH':str(root/'missing-keys')}),
                         ('DP-path-inside-release',{'LARAFASHION_DATA_PROTECTION_PATH':str(release/'wwwroot')})]:
        invalid=env.copy()
        for key,value in change.items():
            if value is None: invalid.pop(key,None)
            else: invalid[key]=value
        before=hashlib.sha256((root/'test.db').read_bytes()).hexdigest()
        result=run(['/usr/sbin/runuser','-u','app','--','dotnet',release/'LaraFashion.dll'],env=invalid,cwd=release,check=False,
                   log='startup-'+name+'.log',timeout=30)
        assert result.returncode!=0 and before==hashlib.sha256((root/'test.db').read_bytes()).hexdigest()
        assert not (root/'missing.db').exists()
        if name=='no-existing-admin':
            with sqlite3.connect(no_admin) as check: assert check.execute('SELECT count(*) FROM AdminUsers').fetchone()[0]==0
        passed('actual-startup-reject-'+name)

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs): return None

def recovery_start_test():
    root=fixture('recovery-start-gate')
    tool(root,'recovery-start-state')
    details=json.loads((root/'recovery-start.json').read_text())
    assert details['ManagedHeapBudgetBytes']==512*1024*1024
    target=pathlib.Path(details['Target']); assert target.is_relative_to(TEMP)
    before={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [target,pathlib.Path(str(target)+'-wal')] if p.exists()}
    result=root/'rejected-restore.json'
    denied=run(['dotnet',TOOL,'--deployment-restore',target,details['Plan'],result],check=False)
    assert denied.returncode!=0 and json.loads(result.read_text())['FailureCode']=='RestoreForbiddenAfterStartOrUnknownAttempt'
    state=json.loads((root/'state.json').read_text())
    assert state['ApplicationStartAttempted'] and not state['DatabaseRestoreAttempted']
    after={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [target,pathlib.Path(str(target)+'-wal')] if p.exists()}
    assert before==after
    passed('independent-CLI-refuses-restore-after-persisted-RecoveryStartAttempted-no-DB-write')

def dotnet_mock_regression():
    root=fixture('dotnet-mock-regression')
    (root/'.larafashion-deploy-harness').write_text('LaraFashion synthetic deployment harness v1')
    mocks=root/'mocks'; mocks.mkdir()
    sentinel=root/'wrong-executable-entered'
    shim=mocks/'dotnet'
    shim.write_text('#!/bin/sh\ntouch '+shlex.quote(str(sentinel))+'\nexit 5\n'); shim.chmod(0o700)
    for executable in ('dotnet',str(shim)):
        env=os.environ.copy(); env.update(LARAFASHION_HARNESS_NATIVE_ROOT=str(root),
            LARAFASHION_HARNESS_DOTNET=executable,PATH=str(mocks)+':'+env['PATH'])
        result=run(['python3',BASE/'deploy/tests/harness.py','--mock','dotnet',TOOL,
            '--maintenance-contract',root/'result.json'],env=env,check=False,timeout=10)
        assert result.returncode!=0 and 'Real dotnet must be an absolute executable outside the mock directory' in result.stdout
        assert not sentinel.exists() and not (root/'result.json').exists()
    passed('dotnet-mock-rejects-bare-or-self-executable-without-recursive-dispatch')

def main():
    phase('environment-and-input-verification')
    for row in metadata['Files']:
        assert hashlib.sha256((BASE/row['Path']).read_bytes()).hexdigest().upper()==row['Sha256']
    shutil.copyfile('/input-manifest.json',EVIDENCE/'input-manifest.json')
    shutil.copyfile('/package-versions.txt',EVIDENCE/'package-versions.txt')
    system=run(['bash','-c','cat /etc/os-release; uname -a; dotnet --info; dotnet --list-sdks; dotnet --list-runtimes; '
        'flock --version; bash --version | head -1; python3 --version; curl --version | head -1; git --version; '
        'df -h / /tmp; stat -f -c "%T" /tmp /evidence'],log='environment.log').stdout
    assert 'Microsoft.AspNetCore.App 9.0.' in system and 'Microsoft.NETCore.App 9.0.' in system
    assert shutil.which('flock')=='/usr/bin/flock'
    (EVIDENCE/'storage.json').write_text(json.dumps({'SyntheticTemp':str(TEMP),'TempFs':run(['stat','-f','-c','%T',TEMP]).stdout.strip(),
        'SQLite':sqlite3.sqlite_version,'Network':'none; IPv4 loopback only','ManagedGcHeapLimitPerProcessMiB':512,'HarnessScenarioWorkers':1,
        'Mocks':'harness systemctl/curl/git/build/fuser; actual published HTTP is separate'}))
    phase('fresh-Linux-Release-build-model-and-publish')
    run(['python3',BASE/'deploy/tests/first-transition-tests.py'],log='first-transition-tests.log')
    run(['dotnet','build','LaraFashion.IntegrationTests/LaraFashion.IntegrationTests.csproj','-c','Release','--no-restore',
         '-p:UseSharedCompilation=false','-p:SourceRevisionId='+HEAD],log='build.log',timeout=300)
    env=os.environ.copy(); env['LARAFASHION_EF_CONNECTION']='Data Source='+str(TEMP/'design-only.db')+';Mode=ReadWrite'
    run(['dotnet','tool','run','dotnet-ef','migrations','has-pending-model-changes','--project','LaraFashion','--configuration','Release','--no-build'],env=env,log='model.log')
    for project,destination in [('LaraFashion',APP),('LaraFashion.IntegrationTests',DROP)]:
        run(['dotnet','publish',project,'-c','Release','--no-build','--no-restore','-o',destination],log='publish-'+project+'.log',timeout=300)
    # Only synthetic executable bundles are readable by the service UID. Fixtures/keys/evidence stay private.
    for directory in (APP,DROP):
        for p in [directory,*directory.rglob('*')]: p.chmod(0o755 if p.is_dir() else 0o644)
    tool(fixture('existing-key-readonly-probe'),'existing-key-probe')
    passed('readonly-existing-key-provider-synthetic-bidirectional-probe')
    lock_process,lock_fd,lock_log,lock_started=lock_tests()
    try:
        phase('full-dotnet-runner')
        result=run(['dotnet',TOOL],log='dotnet-tests.log',timeout=600)
        names=[l for l in result.stdout.splitlines() if l.startswith('PASS ')]
        assert len(names)==91 and 'RESULT: 91/91 passed;' in result.stdout and '\nFAIL ' not in result.stdout
        passed('all-current-dotnet-groups',Count=len(names))
        identity_state_tests()
        recovery_start_test()
        published_app_tests()
        dotnet_mock_regression()
        phase('full-current-Bash-harness')
        env=os.environ.copy(); env.update(LARAFASHION_HARNESS_TOOL=str(TOOL),LARAFASHION_HARNESS_TOOL_DROP=str(DROP),
            LARAFASHION_HARNESS_APP_DROP=str(APP),LARAFASHION_HARNESS_COMMIT=HEAD,LARAFASHION_HARNESS_WORKERS='1',
            LARAFASHION_HARNESS_DOTNET=str(pathlib.Path(shutil.which('dotnet')).resolve()))
        result=run(['bash',BASE/'deploy/tests/run-harness.sh'],env=env,log='bash-tests.log',timeout=1800)
        names=[l.split(';')[0] for l in result.stdout.splitlines() if l.startswith('PASS bash ')]
        assert len(names)==42 and len(set(names))==42 and 'BASH_HARNESS_RESULT: all executed scenarios passed' in result.stdout
        passed('full-current-Bash-plus-Linux-permissions-and-signals',Count=len(names))
        phase('exact-production-lock-timeout-900-seconds')
        assert lock_process.wait(timeout=max(1,930-(time.monotonic()-lock_started)))!=0
        lock_log.flush()
        assert 'Another deployment active' in (EVIDENCE/'lock-timeout.log').read_text()
        assert time.monotonic()-lock_started>=899
        passed('exact-deployment-flock-900-second-timeout-clear-failure')
        phase('final-source-and-binary-fingerprints')
        for row in metadata['Files']: assert hashlib.sha256((BASE/row['Path']).read_bytes()).hexdigest().upper()==row['Sha256']
        (EVIDENCE/'binary-hashes.json').write_text(json.dumps({str(p.relative_to(TEMP)):hashlib.sha256(p.read_bytes()).hexdigest().upper()
            for p in [APP/'LaraFashion.dll',TOOL]}))
        storage_paths=[TEMP/'locks/deployment.lock',TEMP/'identity-state/state.json',TEMP/'service-temp/web/test.db',
            TEMP/'service-temp/web/keys',TEMP/'service-temp']
        harness_roots=list(TEMP.glob('LaraFashionDeployHarness-*'))
        assert harness_roots
        storage_paths.extend([harness_roots[0]/'persistent/data/larafashion.db',harness_roots[0]/'persistent/backups'])
        (EVIDENCE/'filesystem-paths.json').write_text(json.dumps({str(p):run(['stat','-f','-c','%T',p]).stdout.strip() for p in storage_paths},indent=2))
        phase('completed')
    finally:
        if lock_process.poll() is None: lock_process.kill(); lock_process.wait()
        lock_fd.close(); lock_log.close()

try:
    main()
except Exception as error:
    RESULTS.append(dict(Name='acceptance-round',Status='FAIL',Reason=str(error)))
    print('FAIL Linux acceptance: '+str(error),flush=True)
    raise
finally:
    (EVIDENCE/'results.json').write_text(json.dumps(RESULTS,indent=2))
