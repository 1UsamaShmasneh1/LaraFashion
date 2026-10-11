#!/usr/bin/env python3
"""Isolated Bash/SQLite deployment harness. Never invokes SSH/systemd/network."""
import hashlib, json, os, pathlib, shlex, shutil, signal, subprocess, sys, tempfile, time
from concurrent.futures import ProcessPoolExecutor
SELF = pathlib.Path(__file__).resolve()
ROOT = pathlib.Path(os.environ.get("LARAFASHION_HARNESS_NATIVE_ROOT", tempfile.gettempdir()))
REAL_DOTNET = os.environ.get("LARAFASHION_HARNESS_DOTNET", "dotnet")
TOOL = os.environ.get("LARAFASHION_HARNESS_TOOL", "")
MODE = os.environ.get("LARAFASHION_HARNESS_MODE", "")
def call_tool(*args):
    return subprocess.run([REAL_DOTNET, TOOL, *map(str,args)], check=True, stdout=subprocess.DEVNULL)
def synthetic(*args):
    return call_tool("--test-deployment-harness", ROOT, *args)
def trace(event):
    with (ROOT/"trace.log").open("a") as stream: stream.write(event+"\n")
def mock(name, args):
    assert (ROOT/".larafashion-deploy-harness").read_text()=="LaraFashion synthetic deployment harness v1"
    if name=="dotnet":
        executable=pathlib.Path(REAL_DOTNET)
        assert executable.is_absolute() and executable.is_file() and not executable.resolve().is_relative_to(ROOT/"mocks"), "Real dotnet must be an absolute executable outside the mock directory"
    if name=="git":
        if args[:2]==["rev-parse","HEAD"]: print(os.environ["LARAFASHION_HARNESS_COMMIT"])
        elif args[0]=="status" and MODE=="dirty-checkout": print("?? unsaved-server-file.txt")
        elif args[0]!="status": return 2
        return 0
    if name=="mv":
        source,destination=map(pathlib.Path,args[-2:])
        assert source.resolve().is_relative_to(ROOT) and destination.resolve().is_relative_to(ROOT)
        candidate=source.name.startswith(".staging-") and destination.name=="publish"
        if MODE=="swap-first-failure" and destination.name.startswith("previous-"): return 1
        if candidate and MODE in ("swap-second-failure","recovery-rename-failure"): return 1
        if candidate and MODE=="swap-target-collision":
            destination.mkdir(); (destination/"collision.txt").write_text("preserve unexpected destination")
        if MODE=="recovery-rename-failure" and source.name.startswith("previous-"): return 1
        return subprocess.call([os.environ.get("LARAFASHION_HARNESS_BASH","bash"),"-c",'exec /usr/bin/mv "$@"',"mv",*args[:-2],posix(source),posix(destination)])
    if name=="sudo":
        if args[0]=="-u":
            user,args=args[1],args[2:]
            if args[:2] in (["test","-r"],["test","-w"],["test","-x"]):
                if MODE.startswith("permission-"):
                    assert os.name!="nt" and user=="app"
                    return subprocess.call(["/usr/sbin/runuser","-u",user,"--",*args])
                if MODE=="candidate-permission-failure" and "/.staging-" in args[2].replace("\\","/"): return 1
                return 0 if pathlib.Path(args[2]).exists() else 1
        assert args[0] in ("systemctl","fuser")
        return subprocess.call([os.environ.get("LARAFASHION_HARNESS_BASH","bash"),str(ROOT/"mocks"/args[0]),*args[1:]])
    if name=="fuser": return 0 if MODE=="writer-live" else 1
    if name=="sleep": return 0
    if name=="systemctl":
        if args[0]=="show":
            prop=next(x.split("=",1)[1] for x in args if x.startswith("--property="))
            active=(ROOT/"active").read_text()=="1"
            values={"LoadState":"loaded","ActiveState":"active" if active else "inactive","MainPID":"123" if active else "0","WorkingDirectory":posix(ROOT/"persistent/publish"),"ExecStart":posix(ROOT/"persistent/publish/LaraFashion.dll"),"User":"app" if MODE.startswith("permission-") else "harness","EnvironmentFiles":posix(ROOT/"runtime.env")+" (ignore_errors=no)"}
            print(values[prop]); return 0
        if args[0]=="stop":
            trace("stop")
            if MODE=="stop-failure": return 1
            if MODE=="arrival-before-stop" and not (ROOT/"arrived").exists():
                synthetic("write"); (ROOT/"arrived").write_text("yes")
            (ROOT/"active").write_text("0"); return 0
        if args[0]=="start":
            trace("start"); (ROOT/"active").write_text("1")
            manifest=json.loads((ROOT/"persistent/publish/release-version.json").read_text())
            candidate=manifest["Commit"]==os.environ["LARAFASHION_HARNESS_COMMIT"]
            if candidate and MODE in ("health-write","start-write-failure") and not (ROOT/"new-write").exists():
                synthetic("write"); (ROOT/"new-write").write_text("yes")
            return 1 if (candidate and MODE in ("start-failure","start-write-failure")) or (not candidate and MODE=="recovery-start-failure") else 0
        return 2
    if name=="curl":
        trace("GET-health")
        assert "--request" in args and args[args.index("--request")+1]=="GET"
        assert "--location" not in args and "--insecure" not in args
        output=args[args.index("--output")+1]
        if MODE in ("health-failure","health-write","recovery-health-failure"): pathlib.Path(output).write_text("{}")
        else: synthetic("health", output, "wrong" if MODE=="wrong-health" else "good")
        print("200",end=""); return 0
    if name=="flock":
        # Windows lacks util-linux flock. This is only a mock; Linux uses the real utility.
        lock=ROOT/"mock-lock-owner"
        owner=int(lock.read_text()) if lock.exists() else 0
        def alive(pid):
            if not pid: return False
            try: os.kill(pid,0); return True
            except OSError: return False
        if "-n" in args: return 73 if alive(owner) else 0
        deadline=time.time()+30
        while alive(owner):
            if time.time()>deadline: return 1
            time.sleep(.05); owner=int(lock.read_text()) if lock.exists() else 0
        lock.write_text(str(int(os.environ["LARAFASHION_HARNESS_SHELL_PID"])))
        return 0
    if name=="dotnet":
        if args[0] in ("restore","build","tool"):
            if MODE=="build-failure": return 1
            if MODE=="hold-build" and args[0]=="build":
                trace("build-enter")
                with (ROOT/"build-release.fifo").open() as gate: assert gate.readline().strip()=="RELEASE"
                trace("build-exit")
            return 0
        if args[0]=="publish":
            destination=pathlib.Path(args[args.index("-o")+1])
            source=pathlib.Path(os.environ["LARAFASHION_HARNESS_TOOL_DROP" if args[1].endswith("LaraFashion.IntegrationTests.csproj") else "LARAFASHION_HARNESS_APP_DROP"])
            shutil.copytree(source,destination)
            if MODE.startswith("permission-") and not args[1].endswith("LaraFashion.IntegrationTests.csproj"):
                import pwd
                uid=pwd.getpwnam("app").pw_uid
                for path in [destination,*destination.rglob("*")]: os.chown(path,uid,uid)
                if MODE=="permission-deny-read": (destination/"LaraFashion.dll").chmod(0)
                if MODE=="permission-deny-traverse": destination.chmod(0o600)
            if MODE=="missing-tool" and args[1].endswith("LaraFashion.IntegrationTests.csproj"):
                (destination/"LaraFashion.IntegrationTests.dll").unlink()
            return 0
        assert args[0].endswith("LaraFashion.IntegrationTests.dll") and len(args)>1
        command=args[1]; output=pathlib.Path(args[-1])
        if command=="--deployment-backup" and MODE=="backup-failure": return 1
        if command=="--deployment-restore" and MODE=="restore-failure": return 1
        if command=="--deployment-plan" and MODE=="outside-write" and not (ROOT/"outside-write").exists():
            synthetic("write"); (ROOT/"outside-write").write_text("yes")
        if command=="--deployment-apply" and MODE in ("identity-failure","kill-identity","review-failure"):
            synthetic("review-failure" if MODE=="review-failure" else "migrate-identity")
            if MODE=="kill-identity":
                if os.name=="nt":
                    subprocess.run(["taskkill.exe","/PID",(ROOT/"native-shell.pid").read_text(),"/T","/F"],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True)
                else: os.kill(int((ROOT/"shell.pid").read_text()), signal.SIGKILL)
            return 1
        if command=="--deployment-apply" and MODE in ("signal-INT","signal-TERM","signal-HUP"):
            synthetic("migrate-identity")
            os.kill(int((ROOT/"shell.pid").read_text()),getattr(signal,"SIG"+MODE.split("-",1)[1]))
            return 1
        result=subprocess.call([REAL_DOTNET,*args])
        if result: return result
        if command=="--deployment-state" and args[3]=="Completed": trace("completed")
        if command=="--deployment-state" and args[3]=="FilesSwapped" and MODE=="rollback-destination-collision":
            destination=ROOT/"persistent/releases"/("failed-"+pathlib.Path(args[5]).name.removeprefix("previous-"))
            destination.mkdir(); (destination/"collision.txt").write_text("preserve unexpected destination")
            return 1
        if command=="--verify-review-upgrade" and MODE=="zero-result":
            output.write_text('{"ContractVersion":1,"Success":true}'); return 0
        if command=="--prepare-legacy-review" and MODE in ("staging-failure","restore-failure","recovery-start-failure","recovery-health-failure"): return 1
        if command=="--deployment-apply" and MODE=="schema-failure": return 1
        return 0
    return 2

def posix(path):
    value=str(path).replace("\\","/")
    return "/"+value[0].lower()+value[2:] if os.name=="nt" else value
def scenario(base, name, count=1, schema="before", mode="", inherited=False, concurrent=False):
    global ROOT
    ROOT=pathlib.Path(tempfile.mkdtemp(prefix="LaraFashionDeployHarness-"))
    os.umask(0o077)
    (ROOT/".larafashion-deploy-harness").write_text("LaraFashion synthetic deployment harness v1")
    (ROOT/"active").write_text("1")
    for folder in ("persistent/uploads","persistent/keys","persistent/releases","persistent/backups","repo/LaraFashion","repo/LaraFashion.IntegrationTests","mocks"):
        (ROOT/folder).mkdir(parents=True,exist_ok=True)
    for project in ("LaraFashion","LaraFashion.IntegrationTests"):
        (ROOT/f"repo/{project}/{project}.csproj").write_text("<Project/>")
    env=os.environ.copy()
    env.update({"LARAFASHION_HARNESS_NATIVE_ROOT":str(ROOT),"LARAFASHION_HARNESS_MODE":mode,
        "LARAFASHION_DEPLOY_HARNESS_ROOT":posix(ROOT),"LARAFASHION_DEPLOY_CONFIG":posix(ROOT/"no-config"),
        "LARAFASHION_REPOSITORY":posix(ROOT/"repo"),"LARAFASHION_PERSISTENT_ROOT":posix(ROOT/"persistent"),
        "LARAFASHION_LIVE_DIR":posix(ROOT/"persistent/publish"),"LARAFASHION_DATA_PROTECTION_PATH":posix(ROOT/"persistent/keys"),
        "LARAFASHION_SERVICE_ENV_FILE":posix(ROOT/"runtime.env"),
        "LARAFASHION_DEPLOY_LOCK_FILE":posix(ROOT/"deployment.lock"),"LARAFASHION_SERVICE_NAME":"larafashion-harness",
        "LARAFASHION_HEALTH_URL":"http://127.0.0.1:59991/health","LARAFASHION_HARNESS_SHELL_PID":"0"})
    (ROOT/"runtime.env").write_text("\n".join(["ASPNETCORE_ENVIRONMENT=Production","LARAFASHION_DATABASE_PATH="+str(ROOT/"persistent/data/larafashion.db"),"LARAFASHION_UPLOADS_PATH="+str(ROOT/"persistent/uploads"),"LARAFASHION_DATA_PROTECTION_PATH="+str(ROOT/"persistent/keys"),"JwtSettings__SecretKey=synthetic-harness-only-key-0123456789","JwtSettings__Issuer=synthetic","JwtSettings__Audience=synthetic","JwtSettings__ExpiryMinutes=60"]))
    if mode=="bad-runtime-config":
        with (ROOT/"runtime.env").open("a") as stream: stream.write("\nJwtSettings__ExpiryMinutes=0")
    if schema=="identity":
        synthetic_env=env.copy()
    else: synthetic_env=env
    subprocess.run([REAL_DOTNET,TOOL,"--test-deployment-harness",str(ROOT),"create",str(count),schema],env=synthetic_env,check=True,stdout=subprocess.DEVNULL)
    if mode.startswith("permission-"):
        assert os.name!="nt" and os.getuid()==0
        import pwd
        uid=pwd.getpwnam("app").pw_uid
        assert uid!=os.getuid()
        ROOT.chmod(0o711); (ROOT/"persistent").chmod(0o711); (ROOT/"persistent/releases").chmod(0o711)
        for folder in ("data","keys","uploads","publish"):
            top=ROOT/"persistent"/folder
            for path in [top,*top.rglob("*")]: os.chown(path,uid,uid)
        key=ROOT/"persistent/keys/synthetic-key"; key.write_text("synthetic private sentinel"); key.chmod(0o600); os.chown(key,uid,uid)
        for private in (key,ROOT/"persistent/backups"):
            assert subprocess.call(["/usr/sbin/runuser","-u","nobody","--","test","-r",str(private)])!=0
    if concurrent:
        os.mkfifo(ROOT/"build-release.fifo",0o600)
    if schema=="identity" and mode!="missing-evidence":
        source=ROOT/"pre-identity.db"
        env["LARAFASHION_PRE_IDENTITY_EVIDENCE_PATH"]=posix(source)
        env["LARAFASHION_PRE_IDENTITY_EVIDENCE_SHA256"]=hashlib.sha256(source.read_bytes()).hexdigest()
    for command in ("git","sudo","systemctl","fuser","curl","dotnet","sleep","mv"):
        shim=ROOT/"mocks"/command
        shim.write_text("#!/usr/bin/env bash\nexec "+shlex.quote(posix(pathlib.Path(sys.executable)))+" "+shlex.quote(posix(SELF))+" --mock "+command+' "$@"\n')
        shim.chmod(0o700)
    if os.name=="nt":
        shim=ROOT/"mocks/flock"
        shim.write_text("#!/usr/bin/env bash\nexec "+shlex.quote(posix(pathlib.Path(sys.executable)))+" "+shlex.quote(posix(SELF))+' --mock flock "$@"\n')
        shim.chmod(0o700)
    env["PATH"]=str(ROOT/"mocks")+os.pathsep+env["PATH"]
    protected=ROOT/"persistent/backups/old source with spaces.db"; protected.write_bytes(b"protected harness sentinel")
    before=hashlib.sha256(protected.read_bytes()).hexdigest()
    script=base/"deploy/deploy-production.sh"; sha=env["LARAFASHION_HARNESS_COMMIT"]
    bash=env.get("LARAFASHION_HARNESS_BASH","bash")
    if inherited=="flag": env["LARAFASHION_DEPLOY_LOCK_HELD"]="1"
    command='export PATH="$LARAFASHION_DEPLOY_HARNESS_ROOT/mocks:/usr/bin:/bin:$PATH"; export LARAFASHION_HARNESS_SHELL_PID=$$; printf "%s" "$$" > "$LARAFASHION_HARNESS_NATIVE_ROOT/shell.pid"; '
    if inherited is True:
        command+='exec 9>>"$LARAFASHION_DEPLOY_LOCK_FILE"; flock 9; export LARAFASHION_DEPLOY_LOCK_HELD=1; '
    command+="exec bash "+shlex.quote(posix(script))+" "+sha
    with (ROOT/"run.log").open("w") as log:
        process=subprocess.Popen([bash,"-c",command],env=env,stdout=log,stderr=subprocess.STDOUT)
        (ROOT/"native-shell.pid").write_text(str(process.pid))
        try:
            if concurrent:
                assert os.name!="nt", "Concurrent descriptor ownership requires Linux flock"
                deadline=time.time()+30
                while not (ROOT/"trace.log").exists() or "build-enter" not in (ROOT/"trace.log").read_text():
                    assert time.time()<deadline, "First deployment never reached locked build"
                    time.sleep(.05)
                with (ROOT/"concurrent.log").open("w") as other_log:
                    other=subprocess.Popen([bash,"-c",command],env=env,stdout=other_log,stderr=subprocess.STDOUT)
                    try:
                        # A separate open must conflict while the first protected build is held.
                        assert subprocess.call(["/usr/bin/flock","-n","-E","73",str(ROOT/"deployment.lock"),"true"])==73
                        with (ROOT/"build-release.fifo").open("w") as gate: gate.write("RELEASE\n")
                        result=process.wait(timeout=120)
                        deadline=time.monotonic()+30
                        while (ROOT/"trace.log").read_text().splitlines().count("build-enter")!=2:
                            assert time.monotonic()<deadline and other.poll() is None
                            time.sleep(.01)
                        with (ROOT/"build-release.fifo").open("w") as gate: gate.write("RELEASE\n")
                        other_result=other.wait(timeout=120)
                    finally:
                        if other.poll() is None: other.kill(); other.wait()
                    assert other_result==0, "Second serialized deployment failed: "+str(ROOT)
                events=(ROOT/"trace.log").read_text().splitlines()
                assert events.count("completed")==2 and events.count("build-enter")==2
                assert events.index("completed")<len(events)-1-events[::-1].index("build-enter"), "Concurrent deployment entered maintenance before predecessor completed"
            else: result=process.wait(timeout=120)
        except subprocess.TimeoutExpired: process.kill(); process.wait(); raise
    assert hashlib.sha256(protected.read_bytes()).hexdigest()==before, name+": protected retention source deleted"
    expected_success=mode in ("","arrival-before-stop","hold-build","permission-allow") and inherited!="flag"
    assert (result==0)==expected_success, name+": exit mismatch; "+str(ROOT)
    if mode in ("signal-INT","signal-TERM","signal-HUP"):
        expected_code={"signal-INT":130,"signal-TERM":143,"signal-HUP":129}[mode]
        assert result==expected_code and "exit="+str(expected_code) in (ROOT/"run.log").read_text(), name+": signal trap was not proven"
    states=list((ROOT/"persistent/backups/deploy-attempts").glob("*/state.json"))
    before_stop_failure=mode in ("build-failure","missing-tool","bad-runtime-config","candidate-permission-failure","dirty-checkout","permission-deny-read","permission-deny-traverse") or inherited=="flag"
    assert bool(states)!=before_stop_failure, name+": did not reach the intended fault phase; "+str(ROOT)
    if states:
        stage=json.loads(states[-1].read_text())["Stage"]
        expected="Completed" if expected_success else ("Recovered" if mode in ("staging-failure","identity-failure","review-failure","schema-failure","swap-first-failure","swap-second-failure","signal-INT","signal-TERM","signal-HUP") else "ManualRecoveryRequired")
        if mode=="zero-result": expected="Recovered"
        if mode=="kill-identity": expected="SchemaChangeAttempted"
        assert stage==expected, name+": stage "+stage+" != "+expected+"; "+str(ROOT)
        wanted=count+(1 if mode in ("arrival-before-stop","outside-write","health-write","start-write-failure") else 0)
        subprocess.run([REAL_DOTNET,TOOL,"--test-deployment-harness",str(ROOT),"inspect",stage,str(wanted)],env=env,check=True,stdout=subprocess.DEVNULL)
    if before_stop_failure:
        assert "stop" not in (ROOT/"trace.log").read_text() if (ROOT/"trace.log").exists() else True
    if mode in ("swap-target-collision","rollback-destination-collision"):
        collisions=list((ROOT/"persistent").rglob("collision.txt"))
        assert len(collisions)==1 and collisions[0].read_text()=="preserve unexpected destination"
        assert states and json.loads(states[-1].read_text())["DatabaseRestoreAttempted"]
        assert pathlib.Path(json.loads(states[-1].read_text())["BeforeRestorePath"]).is_file()
    if mode=="recovery-health-failure":
        assert (ROOT/"active").read_text()=="0" and "GET-health" in (ROOT/"trace.log").read_text()
        assert json.loads(states[-1].read_text())["ApplicationStartAttempted"]
    if mode=="kill-identity":
        env["LARAFASHION_HARNESS_MODE"]=""
        with (ROOT/"retry.log").open("w") as log:
            retry=subprocess.run([bash,"-c",command],env=env,stdout=log,stderr=subprocess.STDOUT,timeout=120)
        assert retry.returncode!=0
        assert json.loads(states[-1].read_text())["Stage"]=="SchemaChangeAttempted"
    print("PASS bash "+name+"; "+str(ROOT),flush=True)

def main():
    global REAL_DOTNET
    base=pathlib.Path(sys.argv[1]).resolve()
    assert pathlib.Path(TOOL).is_file()
    # Resolve before scenario PATH contains mocks; propagate it to workers and mock subprocesses.
    executable=shutil.which(REAL_DOTNET)
    assert executable, "Real dotnet executable not found"
    REAL_DOTNET=str(pathlib.Path(executable).resolve())
    os.environ["LARAFASHION_HARNESS_DOTNET"]=REAL_DOTNET
    scenarios=[
        ("complete",1,"before",""),("zero",0,"before",""),("over-2730",2733,"before",""),
        ("arrival-before-stop",1,"before","arrival-before-stop"),("outside-write",1,"before","outside-write"),
        ("missing-tool",1,"before","missing-tool"),("zero-result",1,"before","zero-result"),
        ("identity-only",1,"identity",""),("missing-evidence",1,"identity","missing-evidence"),
        ("review-redeploy",1,"review",""),("staging-failure",1,"before","staging-failure"),
        ("identity-failure",1,"before","identity-failure"),("review-failure",1,"before","review-failure"),
        ("schema-failure",1,"before","schema-failure"),("health-write",1,"before","health-write"),
        ("start-write-failure",1,"before","start-write-failure"),("backup-failure",1,"before","backup-failure"),
        ("start-failure",1,"before","start-failure"),("recovery-start-failure",1,"before","recovery-start-failure"),
        ("build-failure",1,"before","build-failure"),("bad-runtime-config",1,"before","bad-runtime-config"),
        ("candidate-permission-failure",1,"before","candidate-permission-failure"),
        ("restore-failure",1,"before","restore-failure"),("wrong-health",1,"before","wrong-health"),
        ("stop-failure",1,"before","stop-failure"),("kill-identity",1,"before","kill-identity"),
        ("dirty-checkout",1,"before","dirty-checkout"),
        ("swap-first-failure",1,"before","swap-first-failure"),("swap-second-failure",1,"before","swap-second-failure"),
        ("swap-target-collision",1,"before","swap-target-collision"),
        ("recovery-rename-failure",1,"before","recovery-rename-failure"),
        ("rollback-destination-collision",1,"before","rollback-destination-collision"),
        ("recovery-health-failure",1,"before","recovery-health-failure"),
    ]
    # Each worker has its own process/global root, database, mocks and lock.
    workers_count=int(os.environ.get("LARAFASHION_HARNESS_WORKERS","3"))
    assert 1<=workers_count<=3, "Invalid harness worker count"
    with ProcessPoolExecutor(max_workers=workers_count) as workers:
        futures=[workers.submit(scenario,base,*args) for args in scenarios]
        for future in futures: future.result()
    scenario(base,"lock-flag-without-descriptor",inherited="flag")
    if os.name=="nt":
        print("LIMITATION: MSYS Bash with mocked flock; actual Linux kernel locks/inherited descriptor remain unverified.",flush=True)
    else:
        scenario(base,"inherited-real-lock",inherited=True)
        scenario(base,"concurrent-real-lock",mode="hold-build",concurrent=True)
        for mode in ("permission-allow","permission-deny-read","permission-deny-traverse","signal-INT","signal-TERM","signal-HUP"):
            scenario(base,mode,mode=mode)
    print("BASH_HARNESS_RESULT: all executed scenarios passed",flush=True)
if __name__=="__main__":
    if len(sys.argv)>2 and sys.argv[1]=="--mock": sys.exit(mock(sys.argv[2],sys.argv[3:]))
    main()
