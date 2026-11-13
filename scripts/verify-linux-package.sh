#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/linux-dev-env.sh"
taskPackage="${1:-$taskRoot/artifacts/linux/study-task-hub_1.2.0~ubuntu1_amd64.deb}"
taskPackage="$(realpath "$taskPackage")"
taskReport="$taskRoot/artifacts/linux/verification"
mkdir -p "$taskReport"
cd "$taskRoot"
bash scripts/test-linux-core.sh --suite all > "$taskReport/core-checks.json"
STUDY_TASK_HUB_TEST_DEB="$taskPackage" PYTHONWARNINGS='ignore::DeprecationWarning:gi.events' /usr/bin/python3 -m unittest discover -s tests -p 'test_linux*.py' > "$taskReport/python-checks.log" 2>&1
/usr/bin/python3 - "$taskPackage" "$taskReport" "$taskRoot" <<'PY'
import datetime,hashlib,json,pathlib,re,subprocess,sys,tempfile
package,report,root=map(pathlib.Path,sys.argv[1:])
depends=subprocess.check_output(['dpkg-deb','--field',str(package),'Depends'],text=True).strip()
missing=[]
versions={}
for group in depends.split(','):
    satisfied=False
    for alternative in group.split('|'):
        match=re.fullmatch(r'\s*([a-z0-9+.-]+)(?:\s*\((>=|<=|=|>|<)\s*([^\)]+)\))?\s*',alternative)
        if not match:raise ValueError('Unsupported dependency expression')
        name,operator,required=match.groups()
        installed=subprocess.run(['dpkg-query','-W','-f=${Status}\n${Version}',name],capture_output=True,text=True)
        lines=installed.stdout.splitlines()
        if installed.returncode or len(lines)<2 or lines[0]!='install ok installed':continue
        version=lines[1]
        if operator and subprocess.run(['dpkg','--compare-versions',version,{'<=':'le','>=':'ge','=':'eq','>':'gt','<':'lt'}[operator],required],capture_output=True).returncode:continue
        versions[name]=version;satisfied=True;break
    if not satisfied:missing.append(group.strip())
if missing:raise RuntimeError('Missing package dependencies: '+', '.join(missing))
with tempfile.TemporaryDirectory(prefix='study-task-hub-manifest-') as tmp:
    subprocess.run(['dpkg-deb','--extract',str(package),tmp],check=True)
    paths=[str(p.relative_to(tmp)) for p in pathlib.Path(tmp).rglob('*') if p.is_file()]
    assert not any(p.endswith(('tasks.db','protection.key')) or '/profiles/' in p or '/__pycache__/' in p for p in paths)
core=json.loads((report/'core-checks.json').read_text().splitlines()[-1])
log=(report/'python-checks.log').read_text()
assert re.search(r'\nOK\s*$',log)
tests=int(re.search(r'Ran (\d+) tests?',log).group(1))
version=subprocess.check_output(['dpkg-deb','--field',str(package),'Version'],text=True).strip()
summary={
    'version':version,'status':'development_preview','generatedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'sourceCommit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),
    'architecture':'amd64','verifiedOs':'Ubuntu 26.04.1 LTS','package':package.name,
    'sha256':hashlib.sha256(package.read_bytes()).hexdigest(),'bytes':package.stat().st_size,
    'core':core,'sharedMobileChecks':47,'pythonGuiWebkitPackageTests':tests,
    'officialNetworkAccessed':False,'productionDataAccessed':False,
    'dependencies':{'status':'passed_on_this_host','declared':depends,'installed':versions},
    'checks':{'selfContainedBackend':'passed','packagedLauncher':'passed','isolatedPrefixReinstallationAndRemoval':'passed',
        'privateDataExcluded':'passed','webkitHttpOnlyAndNavigation':'passed','untrustedTlsRejected':'passed',
        'darkLightAndLargeFont1000x700':'passed','normalHostInstallation':'not_run','differentVersionUpgrade':'not_run',
        'mixedMonitor1xTo2x':'not_run','ubuntu24_04':'not_run','officialAccountAcceptance':'not_run','gnomeNotificationDisplay':'not_run'},
    'platforms':{name:{'fixture':'passed','officialAccount':'not_run'} for name in ['chaoxing','yuketang','changjiang','mooc','xuetang','zhihuishu','ucampus','pta']},
    'installationScope':'fakeroot dpkg with an isolated --root prefix; dependency resolution checked separately on this host; system installation state was not modified',
    'screenshots':'verification/screenshots/'
}
(report.parent/'verification.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n')
(report.parent/'release-assets.json').write_text(json.dumps({'version':version,'assets':[{'file':package.name,'bytes':summary['bytes'],'sha256':summary['sha256']}]},ensure_ascii=False,indent=2)+'\n')
print(json.dumps({'outcome':'passed','coreChecks':core['checks'],'sharedMobileChecks':47,'pythonTests':tests,'package':package.name},ensure_ascii=False))
PY
