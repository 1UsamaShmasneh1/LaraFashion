#!/usr/bin/env python3
"""Bounded first-transition preparation/evidence; the Bash coordinator owns deployment."""
import datetime as dt, hashlib, json, os, pathlib, re, shutil, sqlite3, stat, subprocess, sys, tarfile, urllib.request

BASE = pathlib.Path('/var/www/larafashion')
LIVE = BASE/'publish'
DB = BASE/'data/larafashion.db'
UPLOADS = BASE/'uploads'
KEYS = pathlib.Path('/home/ubuntu/.aspnet/DataProtection-Keys')
AUTH = pathlib.Path('/etc/larafashion/first-transition.json')
LEGACY_COMMIT = 'b196d7268e2b5a5b73b207d963255d5d6a7c0129'
LEGACY_DLL = '50bdd7f65ca6eb81e98f5f4394b317fb40eb125e491490e244b6ddfcef4c5b6b'
PIN = 'SHA256:PY1p4k7jJ1WXl+Mz2ze1wUFQX9oEfM4YKsBAO6gRKws'
UNTIL = '2026-10-11T23:38:00Z'
ENV_FILE = pathlib.Path('/etc/larafashion/runtime.env')
DROPIN = pathlib.Path('/etc/systemd/system/larafashion.service.d/90-first-transition.conf')
MARKER = BASE/'data/first-transition-maintenance'

def require(value, code):
    if not value: raise RuntimeError(code)

def utc(): return dt.datetime.now(dt.timezone.utc)
def stamp(): return utc().isoformat().replace('+00:00','Z')
def digest(path):
    with open(path,'rb') as stream:
        h=hashlib.sha256()
        for block in iter(lambda:stream.read(1024*1024),b''): h.update(block)
        return h.hexdigest()

def save(path, obj, mode=0o600):
    path=pathlib.Path(path); tmp=path.with_name(path.name+'.new')
    with open(tmp,'x',encoding='utf-8') as stream:
        os.chmod(tmp,mode); json.dump(obj,stream,sort_keys=True); stream.flush(); os.fsync(stream.fileno())
    os.replace(tmp,path)
    fd=os.open(path.parent,os.O_RDONLY|os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)

def command(*args): return subprocess.check_output(args,text=True,stderr=subprocess.DEVNULL).strip()
def schema(path):
    with sqlite3.connect('file:'+str(path)+'?mode=ro',uri=True) as db:
        db.execute('pragma query_only=on')
        rows=db.execute("select type,name,tbl_name,sql from sqlite_master where name not like 'sqlite_%' order by type,name").fetchall()
    return hashlib.sha256(json.dumps(rows,ensure_ascii=False,separators=(',',':')).encode()).hexdigest()

def inventory(folder, allow_private=False):
    folder=pathlib.Path(folder); require(folder.is_dir() and not folder.is_symlink(),'UntrustedInventoryRoot')
    rows=[]
    for path in sorted(folder.rglob('*')):
        s=path.lstat(); row={'Path':str(path.relative_to(folder)),'Mode':stat.S_IMODE(s.st_mode),'Uid':s.st_uid,'Gid':s.st_gid}
        if path.is_symlink():
            require(path.resolve() in (UPLOADS,DB,KEYS) or UPLOADS in path.resolve().parents,'UnexpectedApplicationLink')
            row.update(Type='link',Target=os.readlink(path))
        elif path.is_dir(): row.update(Type='directory')
        else:
            require(stat.S_ISREG(s.st_mode) and s.st_nlink==1,'UnexpectedSpecialOrHardlinkedFile')
            require(allow_private or not path.name.lower().endswith(('.key','.pem','.pfx')),'UnexpectedPrivateMaterialInApplication')
            row.update(Type='file',Size=s.st_size,Sha256=digest(path))
        rows.append(row)
    return rows

def remaining(auth, minimum, now=None):
    require(auth['AuthorizedUntilUtc']==UNTIL,'AuthorizationDeadlineMismatch')
    end=dt.datetime.fromisoformat(UNTIL.replace('Z','+00:00'))
    seconds=(end-(now or utc())).total_seconds()
    require(seconds>=minimum,'InsufficientAuthorizationWindow')
    return int(seconds)

def context(run, commit, minimum=0, claimed=True, allow_expired=False):
    require(re.fullmatch(r'lf-cutover-[A-Za-z0-9-]+',run) and re.fullmatch(r'[0-9a-f]{40}',commit),'InvalidTransitionIdentity')
    s=AUTH.lstat(); require(stat.S_ISREG(s.st_mode) and s.st_uid==0 and stat.S_IMODE(s.st_mode)==0o600,'UntrustedAuthorizationFile')
    auth=json.loads(AUTH.read_text())
    require(auth['RunId']==run and auth['CandidateCommit']==commit and auth['HostFingerprint']==PIN and not auth.get('Closed'),'AuthorizationBindingMismatch')
    if not allow_expired: remaining(auth,minimum)
    if claimed: require(auth.get('ClaimedActionRunId')==os.environ.get('LARAFASHION_ACTION_RUN_ID') and re.fullmatch(r'[0-9]+',auth['ClaimedActionRunId']),'ActionAttemptMismatch')
    root=BASE/'backups/first-transition'/run
    require(root.is_dir() and not root.is_symlink() and root.stat().st_uid==1001 and stat.S_IMODE(root.stat().st_mode)==0o700,'UntrustedTransitionRoot')
    return auth,root

def event(root, stage, **extra):
    path=root/'transition-state.json'; state=json.loads(path.read_text()) if path.exists() else {'Events':[]}
    state.update(Stage=stage,Utc=stamp(),**extra); state['Events'].append({'Stage':stage,'Utc':stamp()}); save(path,state)
    os.chown(path,1001,1001)

def process_observation(pid, release=LIVE):
    proc=pathlib.Path('/proc')/str(pid); start=proc.joinpath('stat').read_text().split(') ',1)[1].split()[19]
    env=dict(x.decode().split('=',1) for x in (proc/'environ').read_bytes().split(b'\0') if b'=' in x)
    argv=proc.joinpath('cmdline').read_bytes().decode().strip('\0').split('\0')
    sockets=set()
    for p in (proc/'fd').iterdir():
        try:
            target=os.readlink(p)
            if target.startswith('socket:['): sockets.add(target[8:-1])
        except FileNotFoundError: pass
    listeners=[]
    for table in ('tcp','tcp6'):
        for line in pathlib.Path('/proc/net/'+table).read_text().splitlines()[1:]:
            fields=line.split(); address,port=fields[1].split(':')
            if int(port,16)==5000 and fields[3]=='0A': listeners.append({'Inode':fields[9],'Loopback':address in ('0100007F','00000000000000000000000001000000')})
    db_identity=(DB.stat().st_dev,DB.stat().st_ino)
    handles=[]
    for fd in (proc/'fd').iterdir():
        try:
            s=fd.stat()
            if (s.st_dev,s.st_ino)==db_identity: handles.append(str(fd))
        except FileNotFoundError: pass
    uid=int(next(l for l in (proc/'status').read_text().splitlines() if l.startswith('Uid:')).split()[1])
    observed={'Pid':pid,'ProcessStartTick':start,'PidStable':command('systemctl','show','larafashion.service','-p','MainPID','--value')==str(pid)
              and (proc/'stat').read_text().split(') ',1)[1].split()[19]==start,
              'ServiceActive':command('systemctl','show','larafashion.service','-p','ActiveState','--value')=='active',
              'ProcessPathCorrect':argv==['/usr/bin/dotnet',str(LIVE/'LaraFashion.dll')] and (proc/'cwd').resolve()==LIVE and (proc/'exe').resolve()==pathlib.Path('/usr/share/dotnet/dotnet'),
              'ServiceUserCorrect':uid==1001,'HomeCorrect':env.get('HOME')=='/home/ubuntu',
              'NoIdentityOverrides':not any('dataprotection' in k.lower() or 'contentroot' in k.lower() for k in env),
              'ListenerOwned':bool(listeners) and all(x['Loopback'] and x['Inode'] in sockets for x in listeners),
              'DatabaseHandleCorrect':bool(handles),'DatabaseIdentity':'%s:%s'%db_identity,
              'ApplicationSha256':digest(release/'LaraFashion.dll'),'SchemaDigest':schema(DB)}
    return observed

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs): return None

def legacy_validate(manifest, observed, body, status):
    require(manifest['OriginalCommit']==LEGACY_COMMIT and manifest['ApplicationSha256']==LEGACY_DLL and observed['ApplicationSha256']==LEGACY_DLL,'LegacyBinaryMismatch')
    require(manifest['SchemaDigest']==observed['SchemaDigest'] and manifest['DatabaseIdentity']==observed['DatabaseIdentity'],'LegacyDatabaseOrSchemaMismatch')
    require(all(observed.get(k) is True for k in ('PidStable','ServiceActive','ProcessPathCorrect','ServiceUserCorrect','HomeCorrect','NoIdentityOverrides','ListenerOwned','DatabaseHandleCorrect')),'LegacyProcessIdentityMismatch')
    require(isinstance(observed.get('Pid'),int) and observed['Pid']>0,'InvalidLegacyProcessId')
    require(status==200 and json.loads(body)=={'status':'healthy'},'LegacyHealthContractMismatch')

def encrypt(source, destination, cert):
    subprocess.run(['openssl','cms','-encrypt','-binary','-aes-256-cbc','-in',str(source),'-out',str(destination),'-outform','DER',str(cert)],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    os.chmod(destination,0o600); os.chown(destination,1001,1001)

def archive(source, destination):
    with tarfile.open(destination,'w') as tar:
        for p in sorted(source.iterdir()): tar.add(p,arcname=p.name,recursive=True)
    os.chmod(destination,0o600)

def copy_files(source,destination):
    shutil.copytree(source,destination,symlinks=True,copy_function=shutil.copy2)
    for p in [source,*source.rglob('*')]:
        s=p.lstat(); target=destination/p.relative_to(source)
        os.chown(target,s.st_uid,s.st_gid,follow_symlinks=False)

def prepare_config(root):
    require(not ENV_FILE.exists() and not DROPIN.exists(),'ExistingTransitionConfigNeedsIndependentReview')
    require(command('systemctl','show','larafashion.service','-p','EnvironmentFiles','--value')=='','UnexpectedExistingEnvironmentFiles')
    pid=int(command('systemctl','show','larafashion.service','-p','MainPID','--value'))
    observation=process_observation(pid)
    require(observation['ApplicationSha256']==LEGACY_DLL and all(observation[k] for k in ('PidStable','ProcessPathCorrect','HomeCorrect','NoIdentityOverrides','DatabaseHandleCorrect','ListenerOwned')),'LegacyPreparationIdentityMismatch')
    require(schema(DB)==json.loads(AUTH.read_text())['LegacySchemaDigest'],'ApprovedLegacySchemaChanged')
    old=json.loads((LIVE/'appsettings.json').read_text())
    require('DataProtection' not in old,'UnexpectedDataProtectionConfiguration')
    values={'ASPNETCORE_ENVIRONMENT':'Production','ASPNETCORE_URLS':'http://localhost:5000',
            'LARAFASHION_DATABASE_PATH':str(DB),'LARAFASHION_UPLOADS_PATH':str(UPLOADS),'LARAFASHION_DATA_PROTECTION_PATH':str(KEYS),
            'LARAFASHION_MAINTENANCE_FILE':str(MARKER)}
    for key in ('SecretKey','Issuer','Audience','ExpiryMinutes'):
        value=str(old['JwtSettings'][key]); require(value and not any(c in value for c in "'\"\\\r\n"),'RuntimeValueNeedsReviewedEscaping')
        values['JwtSettings__'+key]=value
    path=root/'runtime.env'
    with open(path,'x',encoding='utf-8') as stream:
        os.chmod(path,0o640); os.chown(path,0,1001)
        stream.write(''.join(k+"='"+v+"'\n" for k,v in values.items())); stream.flush(); os.fsync(stream.fileno())
    require(int(values['JwtSettings__ExpiryMinutes'])>0,'InvalidExistingJwtExpiry')
    save(root/'legacy-recovery.json',{'Contract':'LegacyRecovery.Original.b196d72','RunId':root.name,'OriginalCommit':LEGACY_COMMIT,
          'ApplicationSha256':LEGACY_DLL,'SchemaDigest':observation['SchemaDigest'],'DatabaseIdentity':observation['DatabaseIdentity'],
          'ApplicationFiles':inventory(LIVE),'Keys':inventory(KEYS),'ContentRoot':str(LIVE)+'/',
          'Provenance':'Original DLL/PDB SourceLink and 107 source checksums verified in FirstTransitionOnlinePreflight; current binary hash rechecked',
          'OriginalPid':pid,'ObservedAtUtc':stamp(),'ProcessEvidence':observation})
    os.chown(root/'legacy-recovery.json',1001,1001)
    # Preserve original configurations before any activation. No SSH or TLS private key is included.
    private=root/'original-config'; private.mkdir(mode=0o700)
    paths=[pathlib.Path('/etc/systemd/system/larafashion.service'),pathlib.Path('/etc/larafashion-deploy.env'),LIVE/'appsettings.json']
    dropdir=pathlib.Path('/etc/systemd/system/larafashion.service.d')
    if dropdir.exists(): paths.extend(p for p in dropdir.iterdir() if p.is_file())
    records=[]
    for p in paths:
        require(p.is_file() and not p.is_symlink(),'UntrustedOriginalConfiguration')
        target=private/str(p).lstrip('/'); target.parent.mkdir(parents=True,exist_ok=True,mode=0o700); shutil.copy2(p,target)
        s=p.stat(); os.chown(target,s.st_uid,s.st_gid); records.append({'Path':str(p),'Sha256':digest(p),'Mode':stat.S_IMODE(s.st_mode),'Uid':s.st_uid,'Gid':s.st_gid})
    save(root/'original-config-manifest.json',records); os.chown(root/'original-config-manifest.json',1001,1001)
    event(root,'ConfigurationPreparedUnactivated',JwtSemanticallyEqual=True,LegacyDiscriminator=str(LIVE)+'/',ExistingKeysPreserved=True)

def package(root, snapshot, cert, label):
    manifest=json.loads((root/'legacy-recovery.json').read_text())
    require(inventory(LIVE)==manifest['ApplicationFiles'],'OriginalApplicationChanged')
    require(inventory(KEYS)==manifest['Keys'],'ExistingKeyRingChangedBeforeCutover')
    dest=root/label; dest.mkdir(mode=0o700); ordinary=dest/'ordinary'; secret=dest/'secret'; ordinary.mkdir(mode=0o700); secret.mkdir(mode=0o700)
    copy_files(LIVE,ordinary/'application'); copy_files(UPLOADS,ordinary/'uploads')
    # Separate authentication config and DP material from the general recovery archive.
    for p in (ordinary/'application').rglob('*'):
        if p.is_file() and ('appsettings' in p.name.lower() or p.suffix.lower() in ('.env','.pem','.key','.pfx')):
            target=secret/'application'/p.relative_to(ordinary/'application'); target.parent.mkdir(parents=True,exist_ok=True,mode=0o700); shutil.move(p,target)
    copy_files(KEYS,secret/'data-protection'); copy_files(root/'original-config',secret/'original-config')
    shutil.copy2(root/'runtime.env',secret/'runtime.env'); shutil.copy2(root/'original-config-manifest.json',secret/'original-config-manifest.json')
    shutil.copy2(root/'legacy-recovery.json',ordinary/'legacy-recovery.json')
    if snapshot: shutil.copy2(snapshot,ordinary/'before.db')
    summary={'RunId':root.name,'CutoffUtc':stamp(),'DatabaseIdentity':manifest['DatabaseIdentity'],'OriginalSchemaDigest':manifest['SchemaDigest'],
             'ApplicationFiles':inventory(ordinary/'application'),'Uploads':inventory(ordinary/'uploads'),
             'Secrets':inventory(secret,allow_private=True),'SnapshotSha256':digest(snapshot) if snapshot else None,
             'ContentRoot':str(LIVE)+'/','KeyRepository':str(KEYS),'PreviousContract':'basic health plus independent LegacyRecovery proofs'}
    save(ordinary/'package-manifest.json',summary)
    for name,source in (('recovery',ordinary),('secrets',secret)):
        tar=dest/(name+'.tar'); archive(source,tar); encrypt(tar,dest/(name+'.cms'),cert)
    safe={'RunId':root.name,'Label':label,'CutoffUtc':summary['CutoffUtc'],'Files':{name:{'Sha256':digest(dest/name),'Bytes':(dest/name).stat().st_size} for name in ('recovery.cms','secrets.cms')},
          'ManifestSha256':digest(ordinary/'package-manifest.json'),'UploadsFiles':sum(x['Type']=='file' for x in summary['Uploads']),
          'ApplicationFiles':sum(x['Type']=='file' for x in manifest['ApplicationFiles']),'SnapshotSha256':summary['SnapshotSha256']}
    save(dest/'transfer-manifest.json',safe); os.chown(dest/'transfer-manifest.json',1001,1001); os.chown(dest,1001,1001)
    event(root,label+'Ready',Package=safe)

def receipt(root,label):
    transfer=json.loads((root/label/'transfer-manifest.json').read_text()); proof=json.loads((root/(label+'-external-verified.json')).read_text())
    require(proof['RunId']==root.name and proof['Package']==transfer and proof['DecryptedRestoreVerified'] is True and proof['AllFilesAndModesVerified'] is True,'ExternalRecoveryCopyNotVerified')
    for name,row in transfer['Files'].items(): require(digest(root/label/name)==row['Sha256'],'RecoveryBundleChangedAfterTransfer')

def current_stamp():
    return {'Database':digest(DB),'Wal':digest(str(DB)+'-wal') if pathlib.Path(str(DB)+'-wal').exists() else None}

def main():
    require(os.geteuid()==0,'RootEvidenceProbeRequired')
    op,run,commit=sys.argv[1:4]; auth,root=context(run,commit,0,claimed=op not in ('claim','prepare','practice-package','verify-receipt'),allow_expired=op=='close')
    cert=pathlib.Path(auth['RecipientCertificate']); require(digest(cert)==auth['RecipientCertificateSha256'],'RecipientCertificateChanged')
    if op=='claim':
        remaining(auth,3600); require(not auth.get('ClaimedActionRunId'),'AuthorizationAlreadyConsumed')
        action=os.environ.get('LARAFASHION_ACTION_RUN_ID',''); require(re.fullmatch('[0-9]+',action),'GitHubActionRunRequired')
        auth['ClaimedActionRunId']=action; auth['ClaimedAtUtc']=stamp(); save(AUTH,auth); event(root,'AuthorizationClaimed',ActionRunId=action,CandidateCommit=commit)
    elif op=='deadline': remaining(auth,int(sys.argv[4]))
    elif op=='writers':
        require(command('systemctl','show','larafashion.service','-p','MainPID','--value')=='0','PrimaryWriterStillPresent')
        group=command('systemctl','show','larafashion.service','-p','ControlGroup','--value')
        if group:
            directory=pathlib.Path('/sys/fs/cgroup')/group.lstrip('/')
            if directory.exists(): require(all(not p.read_text().strip() for p in directory.rglob('cgroup.procs')),'ServiceChildStillPresent')
        check=subprocess.run(['fuser','-s',str(DB),str(DB)+'-wal',str(DB)+'-shm'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        require(check.returncode==1,'DatabaseHandleStillPresent')
    elif op=='cutoff':
        save(root/'cutoff.json',{'Utc':stamp(),'Stamp':current_stamp(),'Identity':'%s:%s'%(DB.stat().st_dev,DB.stat().st_ino)})
        event(root,'CutoverBaselineDefined')
    elif op=='old-untouched':
        cutoff=json.loads((root/'cutoff.json').read_text()); manifest=json.loads((root/'legacy-recovery.json').read_text())
        require(current_stamp()==cutoff['Stamp'] and schema(DB)==manifest['SchemaDigest'],'OriginalDatabaseChangedAfterCutoff')
    elif op=='prepare': prepare_config(root)
    elif op=='legacy-health':
        manifest=json.loads((root/'legacy-recovery.json').read_text()); pid=int(command('systemctl','show','larafashion.service','-p','MainPID','--value'))
        observation=process_observation(pid)
        response=urllib.request.build_opener(NoRedirect).open('http://127.0.0.1:5000/health',timeout=5)
        legacy_validate(manifest,observation,response.read(4096),response.status)
        require(inventory(LIVE)==manifest['ApplicationFiles'],'LegacyReleaseFilesMismatch')
        require(command('systemctl','show','larafashion.service','-p','MainPID','--value')==str(pid),'LegacyPidChangedDuringHealth')
        event(root,'LegacyRecoveryHealthVerified',ObservedPid=pid,IndependentEvidence=observation,HealthContract='actual status-only JSON; no manufactured fields')
    elif op=='candidate-check':
        pid=int(command('systemctl','show','larafashion.service','-p','MainPID','--value')); observation=process_observation(pid)
        manifest=json.loads((LIVE/'release-version.json').read_text())
        require(manifest['Commit']==commit and manifest['ApplicationSha256'].lower()==observation['ApplicationSha256'],'CandidateBinaryMismatch')
        require(all(observation[k] for k in ('PidStable','ServiceActive','ProcessPathCorrect','ServiceUserCorrect','HomeCorrect','ListenerOwned','DatabaseHandleCorrect')),'CandidateProcessDatabaseOrListenerMismatch')
        env=dict(x.decode().split('=',1) for x in pathlib.Path('/proc/%s/environ'%pid).read_bytes().split(b'\0') if b'=' in x)
        original=json.loads((root/'original-config/var/www/larafashion/publish/appsettings.json').read_text())['JwtSettings']
        require(all(env.get('JwtSettings__'+k)==str(original[k]) for k in ('SecretKey','Issuer','Audience','ExpiryMinutes')),'CandidateJwtSemanticsChanged')
        require(env.get('LARAFASHION_DATABASE_PATH')==str(DB) and env.get('LARAFASHION_UPLOADS_PATH')==str(UPLOADS)
                and env.get('LARAFASHION_DATA_PROTECTION_PATH')==str(KEYS),'CandidateRuntimePathsMismatch')
        event(root,'CandidateProcessAndConfigurationVerified',ObservedPid=pid,JwtSemanticallyEqual=True,IndependentEvidence=observation)
    elif op=='legacy-compatible':
        manifest=json.loads((root/'legacy-recovery.json').read_text()); previous=pathlib.Path(sys.argv[4]); snapshot=pathlib.Path(sys.argv[5])
        require(inventory(previous)==manifest['ApplicationFiles'] and digest(previous/'LaraFashion.dll')==LEGACY_DLL and schema(snapshot)==manifest['SchemaDigest'],'LegacyRecoveryPairMismatch')
    elif op=='practice-package': package(root,None,cert,'practice')
    elif op=='recovery-package':
        remaining(auth,2700); snapshot=pathlib.Path(sys.argv[4]); require(snapshot.is_file() and not snapshot.is_symlink(),'MissingCutoverSnapshot')
        require(schema(snapshot)==schema(DB),'CutoverSnapshotSchemaMismatch'); package(root,snapshot,cert,'cutover')
    elif op=='verify-receipt': receipt(root,sys.argv[4])
    elif op=='activate':
        remaining(auth,2100); receipt(root,'cutover'); event(root,'ConfigurationActivationAttempted')
        require(not ENV_FILE.exists() and not DROPIN.exists(),'ActivationDestinationChanged')
        require(all(digest(p)==r['Sha256'] for r in json.loads((root/'original-config-manifest.json').read_text()) for p in [pathlib.Path(r['Path'])]),'OriginalConfigChangedBeforeActivation')
        if not ENV_FILE.parent.exists(): ENV_FILE.parent.mkdir(mode=0o750); os.chown(ENV_FILE.parent,0,1001)
        shutil.copy2(root/'runtime.env',ENV_FILE); os.chown(ENV_FILE,0,1001); os.chmod(ENV_FILE,0o640)
        DROPIN.parent.mkdir(mode=0o755,exist_ok=True)
        with open(DROPIN,'x') as stream: stream.write('[Service]\nEnvironmentFile='+str(ENV_FILE)+'\n')
        os.chmod(DROPIN,0o644)
        with open(MARKER,'x') as stream: stream.write(run+'\n')
        os.chown(MARKER,1001,1001); os.chmod(MARKER,0o600)
        subprocess.run(['systemctl','daemon-reload'],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        require(command('systemctl','show','larafashion.service','-p','EnvironmentFiles','--value')==str(ENV_FILE)+' (ignore_errors=no)','ActivatedEnvironmentFileMismatch')
        require(digest(ENV_FILE)==digest(root/'runtime.env'),'ActivatedConfigurationChanged')
        event(root,'ConfigurationActivated',JwtSemanticallyEqual=True)
    elif op=='restore-config':
        state=json.loads((root/'transition-state.json').read_text())
        if any(x['Stage']=='ConfigurationActivationAttempted' for x in state['Events']):
            # Only this attempt's newly created files, with exact content and identity checks.
            if DROPIN.exists(): require(DROPIN.read_text()=='[Service]\nEnvironmentFile='+str(ENV_FILE)+'\n','RecoveryDropInChanged'); DROPIN.unlink()
            if ENV_FILE.exists(): require(digest(ENV_FILE)==digest(root/'runtime.env'),'RecoveryConfigurationChanged'); ENV_FILE.unlink()
            if MARKER.exists(): require(MARKER.read_text()==run+'\n','MaintenanceMarkerMismatch'); MARKER.unlink()
            subprocess.run(['systemctl','daemon-reload'],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        event(root,'OriginalConfigurationRestored')
    elif op=='verify-uploads':
        summary=json.loads((root/'cutover/ordinary/package-manifest.json').read_text())
        require(inventory(UPLOADS)==summary['Uploads'],'OriginalUploadsChanged')
        # DP may create a new technical key after startup; every original key must still match.
        original=json.loads((root/'legacy-recovery.json').read_text())['Keys']; current={x['Path']:x for x in inventory(KEYS)}
        require(all(current.get(x['Path'])==x for x in original),'OriginalDataProtectionKeysChanged')
    elif op=='open':
        require(MARKER.is_file() and not MARKER.is_symlink() and MARKER.read_text()==run+'\n','MaintenanceMarkerMismatch')
        event(root,'PublicReopenAttempted'); MARKER.unlink(); event(root,'PublicReopened')
    elif op=='post-backup':
        path=root/'after.db'; require(not path.exists(),'PostUpgradeBackupAlreadyExists')
        source=sqlite3.connect('file:'+str(DB)+'?mode=ro',uri=True,timeout=1); source.execute('pragma query_only=on')
        destination=sqlite3.connect(path); started=utc()
        def progress(status,remaining_pages,total): require((utc()-started).total_seconds()<90,'OnlinePostBackupDeadline')
        try: source.backup(destination,pages=16,progress=progress,sleep=0.05)
        finally: source.close(); destination.close()
        with sqlite3.connect(path) as check:
            check.execute('pragma journal_mode=delete')
            require(check.execute('pragma integrity_check').fetchall()==[('ok',)] and not check.execute('pragma foreign_key_check').fetchall(),'PostUpgradeBackupIntegrityFailed')
        os.chmod(path,0o600); encrypt(path,root/'after.cms',cert)
        event(root,'PostUpgradeBackupVerified',SnapshotSha256=digest(path),EncryptedSha256=digest(root/'after.cms'),CaptureEndedUtc=stamp())
    elif op=='close':
        auth['Closed']=True; auth['ClosedAtUtc']=stamp(); save(AUTH,auth); event(root,'AuthorizationClosed')
    else: raise RuntimeError('UnknownFirstTransitionOperation')
    print(json.dumps({'Success':True,'Operation':op,'RunId':run,'Utc':stamp(),'RemainingSeconds':remaining(auth,0) if op!='close' else None}))

if __name__=='__main__':
    try: main()
    except Exception as e:
        print(json.dumps({'Success':False,'FailureCode':str(e) if isinstance(e,RuntimeError) else type(e).__name__})); sys.exit(2)
