# 2. A közös Sonar és a teszt-SQL Server

[English](../02-sonar-and-mssql.md) · **Magyar**

[← 1. CI-gép és agent](01-gep-es-agent.md) · [Tartalomjegyzék](README.md) · [Következő: 3. Telepítés projektbe →](03-telepites-projektbe.md)

A toolkit két **gépi szintű**, projektfüggetlen szolgáltatást kezel Dockerben:

| Szolgáltatás | Mire jó | Elérés |
|---|---|---|
| **SonarQube + PostgreSQL** | statikus kódelemzés, security hotspotok, coverage, Quality Gate | `http://<gép>:9000`, a hálózatról is |
| **Teszt-SQL Server** (MSSQL 2022 Developer) | a projektek adatbázisos tesztjei a Sonar coverage-futáshoz | csak a gépen belül: `127.0.0.1,14333` |

Mindkettő gépenként **egy** példány. Minden projekt ugyanazt a Sonart használja, saját
projektkulccsal, és ugyanazt az SQL Servert, saját loginnal.

## 2.1 Telepítés

Ha az `install.bat`-nál mindkét kérdésre igennel feleltél (vagy `--sonar --mssql` kapcsolóval
futtattad), ez már megtörtént. Külön, vagy utólag:

```powershell
security-toolkit infra install --sonar --mssql
```

Kapcsolók:

| Kapcsoló | Jelentés |
|---|---|
| `--sonar` | SonarQube + PostgreSQL telepítése/indítása |
| `--mssql` | teszt-SQL Server telepítése/indítása |
| `--sonar-port <N>` | Sonar host-port (alapból a meglévő, első telepítéskor 9000) |
| `--mssql-port <N>` | SQL Server loopback-port (alapból a meglévő, első telepítéskor 14333) |
| `--upgrade` | az image-ek frissítése (ld. 2.5); enélkül a rögzített verzió marad |

A parancs **idempotens**: bármikor újrafuttatható, a jelszavak és az image-verziók nem változnak.

### Mi hova kerül

```
C:\ProgramData\SecurityToolkit\infra\
  sonar\
    compose.yml   ← a toolkit sablonja, minden futás felülírja; NE szerkeszd
    .env          ← egyszer jön létre: DB-jelszó, port, memórialimit, digest-pinnelt image-ek
  mssql\
    compose.yml
    .env          ← SA-jelszó, port, image-pin
    sql\          ← a login-létrehozó script
```

- A `.env` csak a létrehozó felhasználó, a SYSTEM és az Administrators csoport számára olvasható.
  Megnyitni csak rendszergazdai jogon lehet.
- **Linuxon** ugyanez a `/var/lib/security-toolkit/infra/` alatt van (`sonar/`, `mssql/`), a `.env`
  root tulajdonú és `chmod 600`. Az `infra` parancsokat ezért `sudo`-val futtasd. A lenti
  `C:\ProgramData\SecurityToolkit\infra\...` útvonalak helyett ezt használd.
- **Linuxon a Sonarhoz `vm.max_map_count` ≥ 262144 kell** (a Sonar beépített Elasticsearch-e ez alatt
  nem indul el, a konténer pedig újraindítási hurokba kerül). Az Ubuntu 26.04 alapból 1048576-ot ad,
  sok más disztró csak 65530-at. Az `infra install --sonar` ezt indulás előtt ellenőrzi, és kevés értéknél
  semmit nem ír, hanem megáll (exit 2) a beállítás parancsával. A rendszerparamétert a toolkit nem
  állítja át magától:
  ```bash
  sudo sysctl -w vm.max_map_count=262144
  echo 'vm.max_map_count=262144' | sudo tee /etc/sysctl.d/99-security-toolkit-sonar.conf
  ```
  Windowson a Docker Desktop saját VM-et futtat, ott ezt a Docker Desktop állítja be, a toolkit nem
  ellenőrzi.
- **A `.env`-ben lévő értékek (jelszavak, image-pinek) soha nem változnak meg maguktól.** Ha egy
  értéket kézzel módosítasz (pl. `SONAR_MEM_LIMIT=6g`), a következő `infra install` megtartja.
- A compose-projektnevek fixek: `security-toolkit-sonar` és `security-toolkit-mssql`. A Sonar
  adatai a `security-toolkit-sonar_*` nevű Docker volume-okban vannak.

**Ellenőrzés:**
```powershell
docker ps --format "{{.Names}}  {{.Status}}  {{.Ports}}"
```
Futnia kell a `security-toolkit-sonar-db-1` (healthy), a `security-toolkit-sonar-sonarqube-1` és a
`security-toolkit-mssql-mssql-1` (healthy) konténernek. A Sonar első indulása 1–3 percig tart.

## 2.2 A Sonar első beüzemelése

### Első belépés

1. Böngészőben: `http://localhost:9000` (a CI-gépen) vagy `http://<gép>:9000`.
2. Felhasználó: `admin`, jelszó: `admin`. A Sonar azonnal új jelszót kér. Az új admin-jelszót
   tárold a jelszókezelőben.

### Elérés a hálózatról

A fejlesztők gépéről is el kell érni a Sonart (a böngésző és a SonarLint miatt). A CI-gépen
rendszergazdaként:

```powershell
New-NetFirewallRule -DisplayName "SonarQube 9000" -Direction Inbound -Protocol TCP -LocalPort 9000 -Action Allow -Profile Domain
```

Ha a gépnek van DNS-neve (pl. `ci-gep.<domain>.hu`), a projektekben azt add meg Sonar URL-ként.

> **TLS:** a Sonar itt sima HTTP-n fut. Belső hálózaton ez elfogadható. Ha a hálózaton kívülről is
> elérhető lenne, tegyél elé egy reverse proxyt (IIS/nginx) tanúsítvánnyal. Ezt a toolkit nem
> telepíti.

### Projekt létrehozása – mindig kézzel

1. *Projects* → *Create project* → **Create a local project** (*Manually*).
2. *Project display name* és **Project key**: pl. `MyApp`. A kulcsot írd fel, ez lesz a
   projekt `sonar.projectKey`-e.
3. *Main branch name*: a repó fő ága (`master`/`main`).
4. *New code*: „Previous version” vagy „Number of days”, a csapat döntése szerint.

> **Ne a „With Azure DevOps” opciót válaszd.** Az a megjelenített névtől eltérő, belső kulcsot
> generál (`<AzureProjekt>_<repó>_<GUID>`). A toolkit ezzel a kulccsal nem fog feltölteni, és a hiba
> jogosultsági hibának látszik. Ha mégis így jött létre: *Project Settings* → *Update Key* → egy
> ember által olvasható kulcsra átírni.

### Project analysis token

1. A projekt oldalán: *Project Settings* → *Permissions*. Ellenőrizd, hogy a tokent generáló
   felhasználónak van **Execute Analysis** joga. A Sonar-adminisztrátori jog ezt **nem** adja
   automatikusan.
2. Jobb felső sarok → *My Account* → *Security* → *Generate Tokens*:
   - *Type*: **Project Analysis Token**, *Project*: az előbbi projekt;
   - *Expires in*: a szervezeti szabály szerint (pl. 1 év).
3. A tokent egyszer mutatja meg. Ez lesz a pipeline `SONAR_TOKEN` secretje
   ([4. fejezet](04-pipeline.md#42-pipeline-változók-és-secretek)). **Csak a pipeline-ba kerül**,
   fejlesztői gépre nem: helyben mindenki a saját user tokenjét használja (ld. alább).

**Ellenőrzés** (bármelyik gépről, ahonnan a Sonar elérhető):
```powershell
$token = Read-Host 'Token'
curl.exe -s -u "${token}:" "http://<gép>:9000/api/authentication/validate"
```
Válasz: `{"valid":true}`.

### Személyes user token (helyi futtatáshoz)

A két token szerepe különböző, és nem cserélhetők fel:

| | Project analysis token | User token |
|---|---|---|
| Kinek | a pipeline-nak (projektenként egy) | a fejlesztőnek (személyenként egy) |
| Hatókör | egyetlen projekt, csak elemzés-feltöltés | minden projekt, amihez a felhasználónak joga van; feltöltés **és** API-olvasás (issue-k, hotspotok) |
| Hol él | a pipeline `SONAR_TOKEN` secret változójában | a fejlesztő gépén, a `SONAR_TOKEN` környezeti változóban, munkamenetenként |

Mivel helyben csak egy `SONAR_TOKEN` létezhet, **egyetlen user tokennel** dolgozz minden
projektben. A projektet a `security.config.json` `sonar.projectKey`-e választja ki, nem a token.

1. A Sonar-admin minden olyan projektnél, ahová a fejlesztő helyből is feltölt, adja meg neki (vagy a
   fejlesztők csoportjának) az **Execute Analysis** jogot: *Project Settings* → *Permissions*.
   Csak olvasáshoz (pl. a [8. fejezet](08-llm-tamogatott-javitas.md) munkamenetéhez) a *Browse* is
   elég.
2. A fejlesztő: *My Account* → *Security* → *Generate Tokens* → *Type*: **User Token**, *Expires
   in*: a szervezeti szabály szerint.
3. Használat: munkamenetenként, `$env:SONAR_TOKEN = Read-Host 'Sonar user token'`
   ([6.1](06-helyi-futtatas-es-cli.md#61-a-scan-parancs)).

> **Ne `setx`-szel tárold.** A `setx` a tokent titkosítatlanul a registrybe (`HKCU\Environment`)
> írja, és minden későbbi folyamat örökli. Ha nem akarod minden alkalommal begépelni, egy
> titoktárból (pl. a PowerShell `SecretManagement` modul) töltsd be a munkamenetbe.

> **Ne a project analysis tokent használd helyben.** Az csak egy projektre jó: egy másik projektben
> „not authorized” hibával bukik, és ha több projekten dolgozol, folyton cserélgetni kellene.

### Quality Gate

Az alapértelmezett *Sonar way* Quality Gate az **új kódra** állít feltételeket (coverage ≥ 80%,
duplikáció, megbízhatósági/biztonsági besorolás). A coverage-feltétel csak akkor teljesülhet, ha a
coverage-import be van kapcsolva ([3.5](03-telepites-projektbe.md#35-sonar-coverage)).

- **Report módban** (alapértelmezés) a pipeline nem vár a Quality Gate-re, csak feltölt.
- **Gate módban** a Sonar-lépés megvárja a Quality Gate eredményét, és bukó Quality Gate-nél a lépés
  is elbukik ([5.4](05-kiertekeles.md#54-report-vagy-gate-mód)).

## 2.3 Teszt-SQL Server: projektlogin létrehozása

Minden projekt saját SQL-loginnal fut. Ez `CREATE ANY DATABASE` jogot kap, így a tesztjei maguk
hozhatják létre és dobhatják el a saját adatbázisaikat. Szándékosan **nem** `dbcreator`, mert az
bármelyik másik projekt adatbázisát is eldobhatná.

A CI-gépen:

```powershell
security-toolkit infra add-test-login myapp_test
```

- A login neve betűvel kezdődik, és csak betűt, számot és `_`-t tartalmaz (3–64 karakter).
- A parancs kiírja a teljes connection stringet a jelszóval együtt. **A jelszót csak ekkor látod.**
  Másold be azonnal a pipeline secretjébe ([4.2](04-pipeline.md#42-pipeline-változók-és-secretek)).
- Újrafuttatva **új jelszót** generál, a régi érvényét veszti.

A kiírt connection string formája:

```
Server=127.0.0.1,14333;User Id=myapp_test;Password=<jelszó>;Encrypt=True;TrustServerCertificate=True
```

> **`127.0.0.1`, ne `localhost`.** A konténer portja csak az IPv4 loopbacken figyel. A `localhost` az
> IPv6 `::1`-re is feloldódhat, ahol senki nem figyel, ezért az SQL-kliens
> „actively refused” vagy „The wait operation timed out” hibát ad, és a DB-t használó tesztek
> tömegesen elbuknak.

**Ellenőrzés** (Windows PowerShell 5.1-ben, a CI-gépen):
```powershell
$cs = Read-Host 'Connection string'
$c = New-Object System.Data.SqlClient.SqlConnection "$cs;Connect Timeout=15"; $c.Open(); $c.ServerVersion; $c.Close()
```
Egy verziószámot ír ki (pl. `16.00.xxxx`).

> **Az SQL Servernek nincs adat-volume-ja, mert a tesztadat eldobható.** Egy sima újraindítás
> (`docker restart`, a gép újraindítása) megtartja a loginokat. Egy `docker compose down` + `up`
> viszont mindent töröl: utána **minden projekt loginját újra létre kell hozni**, és a pipeline
> secreteket frissíteni.

## 2.4 Napi üzemeltetés

A compose-mappában dolgozz (rendszergazdai PowerShell):

```powershell
cd C:\ProgramData\SecurityToolkit\infra\sonar
docker compose ps                    # állapot
docker compose logs -f sonarqube     # Sonar-log élőben
docker compose restart sonarqube     # Sonar újraindítása (adat megmarad)
```

Mindkét stack `restart: unless-stopped`, ezért a Docker (Desktop vagy Linuxon az Engine-szolgáltatás) indulásakor maguktól elindulnak.

**Memória:** a Sonar limitje a `.env` `SONAR_MEM_LIMIT` értéke (alapból `4g`), az SQL Serveré
`MSSQL_MEMORY_LIMIT_MB` (alapból `2048`) és egy 3 GB-os konténerlimit. Módosítás után:
`security-toolkit infra install --sonar` (vagy `--mssql`).

**Port:** `security-toolkit infra install --sonar --sonar-port 9100`. Ez gépi beállítás, a projektek
`sonar.url`-jét is át kell írni.

## 2.5 Frissítés, mentés, átköltözés

### Image-frissítés

```powershell
security-toolkit infra install --sonar --upgrade
```

Ez újra letölti a forrás-tageket (`sonarqube:community`, `postgres:17`), és az új digestet rögzíti a
`.env`-ben. **Előtte készíts mentést** (lent). Egy nagyobb Sonar-verzióugrás után a Sonar
karbantartási módban indulhat. Ekkor nyisd meg a `http://<gép>:9000/setup` oldalt, és indítsd el az
adatbázis-migrációt.

> A `postgres` főverzióját (`17`) a toolkit rögzíti. Egy PostgreSQL-főverzióváltás dump/restore-t
> igényel, ezt az `--upgrade` nem végzi el.

### Mentés (ajánlás)

A Sonar teljes állapota (projektek, tokenek, triázs-döntések) a PostgreSQL-adatbázisban van:

```powershell
docker exec security-toolkit-sonar-db-1 pg_dump -U sonar -d sonar -Fc -f /tmp/sonar.dump
docker cp security-toolkit-sonar-db-1:/tmp/sonar.dump D:\Backup\sonar-$(Get-Date -Format yyyyMMdd).dump
```

Ütemezd hetente (Task Scheduler), és vidd a mentést egy másik gépre. Mellé mentsd el a
`C:\ProgramData\SecurityToolkit\infra\sonar\.env`-t is: abban van a DB-jelszó.

### Átköltözés új gépre (ajánlás; élesben még nem próbáltuk)

1. Az új gépen végezd el az [1. fejezetet](01-gep-es-agent.md), de az `install.bat`-ot
   **`--no-infra`**-val futtasd.
2. Másold a régi gép `sonar\.env` fájlját az új gépre, ugyanoda:
   `C:\ProgramData\SecurityToolkit\infra\sonar\.env`. Így ugyanaz a DB-jelszó és ugyanaz az
   image-verzió lesz.
3. `security-toolkit infra install --sonar`. Ez egy üres adatbázissal indul.
4. Állítsd le a Sonart, töltsd vissza a dumpot, és indítsd újra:
   ```powershell
   cd C:\ProgramData\SecurityToolkit\infra\sonar
   docker compose stop sonarqube
   docker cp D:\Backup\sonar.dump security-toolkit-sonar-db-1:/tmp/sonar.dump
   docker exec security-toolkit-sonar-db-1 pg_restore -U sonar -d sonar --clean --if-exists /tmp/sonar.dump
   docker compose start sonarqube
   ```
5. A teszt-SQL Servert egyszerűen telepítsd újra (`infra install --mssql`), és hozd létre újra a
   loginokat. Új jelszavakat kapnak, ezért a pipeline secreteket frissíteni kell.

> **Ha a `.env` elveszik, de a `security-toolkit-sonar_postgres_data` volume még megvan**, az
> `infra install` szándékosan megtagadja a telepítést. Új jelszóval a Sonar kizárná magát a saját
> adatbázisából. Ilyenkor vagy visszaállítod a `.env`-t a mentésből, vagy tudatosan törlöd a volume-ot
> (`docker volume rm security-toolkit-sonar_postgres_data`). Ez utóbbi minden Sonar-adatot és tokent
> töröl, az összes projektre.

## 2.6 Ellenőrzőlista

- [ ] Mindhárom konténer fut (`docker ps`)
- [ ] Sonar: admin-jelszó lecserélve, tűzfalszabály, elérhető a fejlesztői gépekről
- [ ] Sonar-projekt **kézzel** létrehozva, a kulcs feljegyezve
- [ ] Project analysis token (a pipeline-nak), `api/authentication/validate` → `valid: true`
- [ ] A helyben futtató fejlesztőknek *Execute Analysis* jog a projekten; mindenki saját **user tokent** használ
- [ ] Teszt-SQL login létrehozva, a connection string (127.0.0.1) eltárolva, a próbacsatlakozás sikeres
- [ ] Heti Sonar-mentés ütemezve, a `.env` elmentve

[Következő: 3. Telepítés projektbe →](03-telepites-projektbe.md)
