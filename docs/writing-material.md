# Yazı malzemesi — FlowLens'te öğrenilenler

> Bu dosya bir anlatı değil, **ham malzeme**. Her madde: ne bilmiyorduk · nasıl ortaya çıktı ·
> doğrusu · kodda nerede · sürpriz değeri.
>
> **Sürpriz değeri kriteri:** Microsoft dokümantasyonunda net yazan bir şey **DÜŞÜK**.
> Tutorial'larda geçmeyen, ancak gerçek bir kod tabanında karşılaşılan **YÜKSEK**.
>
> Kaynak: `phase-1..7-notes.md`, `known-limitations.md` (L1–L24), `phase2-validation.md`,
> `phase3-validation.md`, `evals/report.md`.

---

# LİSTE 1 — Roslyn ve statik analiz altyapısı

## 1.1 Sembol kimliği ve çözümleme

### R1 — Sembol kimliği **compilation başına**, solution başına değil · **YÜKSEK**

- **Yanlış bildiğimiz:** Plan §5'te *"tek `Solution` canlı tutulacağı için sembol kimliği stabil"*
  yazılmıştı. Yazılı bir varsayımdı, sorgulanmamıştı.
- **Nasıl çıktı:** **Sessiz yanlış cevap.** Prefix'ler düzeldikten sonra zincir çalıştı ama
  `PUBLISHES` kenarı **hiç oluşmadı**; `OrderPaid` "internal domain event" diye sınıflandı. Hata
  yok, uyarı yok — modüller arası **tek köprü** sessizce hiç kurulmuyordu.
- **Doğrusu:** Kimlik **bir compilation içinde** stabildir. `Ordering.Domain.Orders.OrderPaid`,
  `Ordering.Domain` ile `Ordering.Infrastructure` compilation'larında **farklı `ITypeSymbol`
  instance'ları** ve `SymbolEqualityComparer.Default` bunları eşit görmüyor.
- **Kodda:** Projeler arası eşleme yapan her sözlük tam nitelikli **isimle** anahtarlanıyor —
  `NodeId.ForType` / `NodeId.ForMethod` (`src/FlowLens.Core/NodeId.cs:57-61`).
  `ImplementationResolver` cache'i de öyle: sembolle anahtarlansaydı *çalışırdı* ama neredeyse her
  çağrıda ıskalardı.
- **Kaynak:** `phase-2-notes.md §4.2`

> Anlatı değeri: yanlış varsayım **yazılıydı**, ve onu çürüten şey bir hata mesajı değil, olmayan
> bir kenardı.

### R2 — Extension method: **reduced** ve **unreduced** iki farklı sembol · **YÜKSEK**

- **Bilmiyorduk:** Bir extension method'un çağrı yerindeki sembolü ile gövdesinden alınan
  sembolünün **aynı olmadığını**.
- **Nasıl çıktı:** İlk çalıştırmada **25 endpoint'in 24'ü `unresolved route`**. Yalnız `GET /`
  çalıştı — çünkü tek o extension method'dan geçmiyordu.
- **Doğrusu:** `group.MapOrderEndpoints()` çağrı yerinde **reduced** forma bağlanır (`this`
  parametresi düşmüş); gövde içindeki `GetEnclosingSymbol` **unreduced** formu döndürür
  (`MapOrderEndpoints(IEndpointRouteBuilder)`). İki farklı sözlük anahtarı → prefix yayılımı
  hedefini hiç bulamıyor.
- **Kodda:** `NodeId.Canonical` = `(symbol.ReducedFrom ?? symbol).OriginalDefinition`
  (`src/FlowLens.Core/NodeId.cs:54-55`), ve sembolün **kimlik olarak kullanıldığı her yerde**.
- **Kaynak:** `phase-2-notes.md §4.1`, `known-limitations.md L1`

### R3 — `OriginalDefinition` olmadan yürüyüş **yakınsamıyor** · **YÜKSEK**

- **Bilmiyorduk:** Generic bir metodun her instantiation'ının ayrı bir sembol olduğunu ve bunun
  BFS'in `visited` kümesini işlevsiz bıraktığını.
- **Nasıl çıktı:** Tasarım sırasında; `Select<A,B>`, `Select<C,D>`… ayrı düğümler olarak birikiyor.
- **Doğrusu:** `OriginalDefinition` generic instantiation'ları tek düğüme çöktürür. Yürüyüşün
  sonlanması buna bağlı — performans değil **doğruluk** meselesi.
- **Kodda:** `NodeId.cs:45-46` (XML yorumu bunu "load-bearing" diye kaydediyor), `:54-55`.
- **Kaynak:** `NodeId.cs` yorumu, `phase-2-notes.md §5`

### R4 — `SaveChangesAsync`'in `ContainingType`'ı **her zaman `DbContext`** · **YÜKSEK**

- **Yanlış bildiğimiz:** Çağrılan metodun `ContainingType`'ının, çağrının hangi modüle ait
  olduğunu vereceği.
- **Nasıl çıktı:** **Sessiz yanlış cevap.** `SaveChangesWithEntityParameter` mekanizması **hiç
  kenar üretmedi** — sıfır. Hata yok, sadece boş sonuç.
- **Doğrusu:** `SaveChangesAsync` `DbContext`'in **kendisinde** bildirilmiş, dolayısıyla
  `ContainingType` her zaman `Microsoft.EntityFrameworkCore.DbContext`. Hangi modülün modeli olduğu
  **yalnız alıcının tipinde**: `GetTypeInfo(access.Expression)`.
- **Kodda:** `src/FlowLens.Core/Ef/EntityAccessAnalyzer.cs:117-124` — yorum tam olarak bunu
  anlatıyor. Sonuç: 0 → **10 kenar**.
- **Kaynak:** `phase-3-notes.md §5.1`

### R5 — `SymbolDisplayFormat.FullyQualifiedFormat` **tipler için** tasarlanmış · **ORTA**

- **Bilmiyorduk:** Bir metot sembolünde içeren tipi **atladığını**.
- **Nasıl çıktı:** Demo çıktısında ilginç kısım `ValidateAsync` diye görünüyordu — hangi tipin
  metodu olduğu yok.
- **Doğrusu:** Metotlar için `IncludeContainingType | IncludeParameters` taşıyan **ayrı bir
  format** gerekiyor.
- **Kodda:** `NodeId.MemberFormat` (`src/FlowLens.Core/NodeId.cs:20-28`),
  `SemanticModelDemo.MemberFormat`.
- **Kaynak:** `phase-1-notes.md §5`

### R6 — `GetDeclaredSymbol` taban sınıftan `ISymbol` döner · **DÜŞÜK**

- **Doğrusu:** `IMethodSymbol` almak için `using Microsoft.CodeAnalysis.CSharp;` (CSharpExtensions)
  gerekiyor. Derleme zamanı sürtünmesi, sessiz hata değil.
- **Kaynak:** `phase-1-notes.md §5`. **Dokümante — düşük değer.**

---

## 1.2 MSBuild, yükleme ve assembly kimliği

### R7 — `MSBuildLocator`: sorun **satır sırası değil, JIT metot sınırı** · **YÜKSEK**

- **Yanlış bildiğimiz:** Roadmap *"ilk satırda çağrılmalı"* diyordu. Yetersiz bir formülasyon.
- **Nasıl çıktı:** `FileNotFoundException: Microsoft.Build` — `RegisterDefaults()` **ilk satır
  olmasına rağmen**.
- **Doğrusu:** CLR bir metodu ilk çağrıldığında derler ve derlerken **gövdesinde adı geçen her
  tipin** assembly'sini resolve eder. `Main`'e girildiği anda `MSBuildWorkspace` resolve edilir,
  yani `RegisterDefaults()` **henüz çalışmamıştır**. Ayrım **metot sınırıyla** yapılır.
- **Kodda:** `src/FlowLens.Cli/Program.cs:16,19` (locator, sonra ayrı bir tipe çağrı) ·
  `src/FlowLens.Cli/Runner.cs:41-42` — **`[MethodImpl(MethodImplOptions.NoInlining)]` dekorasyon
  değil, taşıyıcı**: inline edilirse gövde `Main`'in JIT'ine karışır ve aynı tuzağa düşülür.
  Test tarafında `[ModuleInitializer]` (`tests/FlowLens.Tests/TestModuleInitializer.cs:28,33`).
- **Kaynak:** `phase-1-notes.md §3`

> **Anlatının en iyi açılışlarından biri.** Dokümantasyon *"MSBuild tiplerini kullanmadan önce
> çağır"* der; **neden** yetmediğini ve `NoInlining`'in neden zorunlu olduğunu söylemez.

### R8 — MSBL001: locator'ın **kendi paket zinciri** onu devre dışı bırakıyor · **YÜKSEK**

- **Bilmiyorduk:** `Microsoft.CodeAnalysis.Workspaces.MSBuild`'in `Microsoft.Build.Framework`'ü
  transitive getirdiğini ve bunun locator'ı bozduğunu.
- **Nasıl çıktı:** İlk build hata verdi: `error MSBL001: A PackageReference to the package
  'Microsoft.Build.Framework' at version '17.11.48' is present … without ExcludeAssets="runtime"`.
- **Doğrusu:** O assembly `bin/`'e kopyalanırsa runtime'da **iki kopya** olur ve MSBuildLocator'ın
  resolver'ı devre dışı kalır — **locator'ın engellemek için var olduğu hata, kendi bağımlılık
  zincirinden geliyor.**
- **Kodda:** `Directory.Build.props` — `ExcludeAssets="runtime"` + `PrivateAssets="all"`, sürüm
  transitive olanla birebir pinlenmiş (NU1605 önlemi).
- **Ek:** `MSBuildWorkspace`, `Microsoft.CodeAnalysis.CSharp.Workspaces` paketinde **değil**;
  ayrı paket (`Microsoft.CodeAnalysis.Workspaces.MSBuild`). Roadmap'in paket listesinde eksikti.
- **Kaynak:** `phase-1-notes.md §3`

### R9 — `OpenSolutionAsync` **exception atmaz**, sessizce eksik `Solution` döner · **YÜKSEK**

- **Bilmiyorduk:** Bir proje yüklenemezse yüklemenin **temiz görüneceğini**.
- **Nasıl çıktı:** Tasarım incelemesinde yakalandı — ama fark edilmeseydi Faz 2'de *"bu metot hiç
  çağrılmıyor"* gibi **güvenle yanlış** sonuçlar üretecekti.
- **Doğrusu:** Dört **bağımsız** sinyal gerekiyor, tek başına hiçbiri yetmiyor:
  1. `RegisterWorkspaceFailedHandler` (`SolutionLoader.cs:51-54`)
  2. `.sln`'den sayılan proje ↔ yüklenen proje (`SolutionLoader.cs:45,86`) — **event kaçsa bile
     yakalar**
  3. `project.SupportsCompilation` (`MethodScanner.cs:37`)
  4. `compilation.GetDiagnostics()` (`--check-compilation`)
- **Kritik detay:** **Handler `OpenSolutionAsync`'ten ÖNCE bağlanmalı** — replay buffer'ı yok,
  sonra bağlanan hiçbir şey görmez ve yükleme **temiz görünür**.
- **Ek:** Diagnostic'ler `(Kind, Message)` ile dedupe ediliyor; MSBuildWorkspace aynı mesajı
  etkilenen her proje için ayrı fırlatıyor.
- **Kaynak:** `phase-1-notes.md §4`

### R10 — `Assembly.LoadFrom` .NET Core'da **ayrı bir bağlam değil** · **YÜKSEK**

- **Yanlış bildiğimiz:** "LoadFrom ile yüklersem izole olur."
- **Nasıl çıktı:** Hedefin derlenmiş DbContext'lerini aynı sürece yükleme tasarımı yapılırken; üç
  tasarım denendi, ikisi **ölçümle** elendi.
- **Doğrusu:** `LoadFrom` = `Default.LoadFromAssemblyPath` + o dosyanın klasörüne bakan **catch-all
  `Default.Resolving` probe'u**. Tam bağımlılık kapanışı olan tek klasör (Host çıktısı)
  `Microsoft.CodeAnalysis*` **5.0.0.0** ve `Microsoft.Build.Framework` **15.1.0.0** taşıyordu —
  yani bu yol **MSBL001 düzeltmesini arka kapıdan iptal ederdi**.
- **Kodda:** Reddedildi; `src/FlowLens.Core/Ef/TargetModelLoadContext.cs` hibrit ALC'yi uyguluyor.
  `Assembly.LoadFrom` **hiç kullanılmıyor**, `Default.Resolving`'e **hiç dokunulmuyor**.
- **Kaynak:** `phase-3-notes.md §3`

### R11 — Tam izolasyon **imkânsız**: `AssemblyDependencyResolver` bilerek `null` döner · **YÜKSEK**

- **Yanlış bildiğimiz:** *"İzole ALC kullanırsam süreçte EF Core'un tek kopyası olur."*
- **Nasıl çıktı:** `FileNotFoundException` — izole ALC'ye yüklenen EF Core
  `Microsoft.Extensions.Caching.Memory` istiyor, ADR `null` dönüyor, Default'a düşüyor,
  FlowLens'in TPA'sında yok.
- **Doğrusu:** ADR **shared-framework** assembly'leri için bilerek `null` döner (deps.json'da
  yokturlar, Default'la birleşsinler diye). Yani izolasyon iddiası baştan yanlıştı.
- **Kodda:** `TargetModelLoadContext.cs:23-25` (yorum bunu kaydediyor), `:85-86` (EF/Npgsql
  Default'a bırakılıyor), `:102` (`Microsoft.Extensions.*` önce Default, bulamazsa hedefin
  kopyası), `:109`/`:145` (`null` → Default).
- **Kaynak:** `phase-3-notes.md §3`

### R12 — TPA **basit isimle** bağlar, **sürümü umursamaz** · **YÜKSEK**

- **Bilmiyorduk:** Sürüm uyuşmazlığının bir yükleme hatası **vermeyeceğini**.
- **Nasıl çıktı:** Tasarımda öngörüldü, sonra **elle kanıtlandı**: EF sürümü 10.0.9 → 10.0.4
  düşürülüp `build` koşuldu.
- **Doğrusu:** Uyuşmayan sürüm **sessizce bağlanır**, sonra model kurulurken **alakasız bir
  noktada** `MissingMethodException`/`TypeLoadException` olarak patlar.
- **Kodda:** `EfVersionGate` — Host'un `deps.json`'ını okur (modülünkini değil: modül 10.0.4,
  Host 10.0.9 diyor) ve "aynı major, FlowLens ≥ hedef" kuralını **build'i durdurarak** uygular.
  Sonuç: exit **6**, `graph.json` **yazılmıyor**. Kaçış bayrağı **bilerek eklenmedi**.
- **Kaynak:** `phase-3-notes.md §3`, `known-limitations.md L14`

### R13 — Classlib `bin/` NuGet varlıklarını **taşımaz** · **ORTA-YÜKSEK**

- **Bilmiyorduk:** Bir modülün `OutputFilePath`'ini probing kökü yapmanın yetmeyeceğini.
- **Nasıl çıktı:** Ölçüldü: `Ordering.Infrastructure/bin/…` → **11 dll**, EF Core **YOK**.
  `Host/bin/…` → **173 dosya**, tüm modüller + EF Core + Npgsql + deps.json.
- **Doğrusu:** Tam bağımlılık kapanışı **yalnız uygulama çıktısında**. Modül `bin`'ini kök yapmak
  `FileNotFoundException` ile biterdi.
- **Kodda:** Probing kökü Host çıktısı; `EfProbe` + `TargetModelLoadContext`.
- **Kaynak:** `phase-3-notes.md §3`

### R14 — EF'in `ApplyConfigurationsFromAssembly`'si `ReflectionTypeLoadException`'ı **yutuyor** · **ORTA**

- **Bilmiyorduk:** Yüklenemeyen bir tipin **sessizce düşeceğini** — düşen bir
  `IEntityTypeConfiguration` = **sessizce kaybolan bir tablo**.
- **Nasıl çıktı:** `EfProbe` `GetTypes()`'ı **bizzat çağırdığı** ve `LoaderExceptions`'ı
  raporladığı için görüldü. 3 Infrastructure assembly'sinde birer tip yüklenemiyordu.
- **Doğrusu:** Kütüphanenin yuttuğu istisnayı **kendin çağırıp** görmen gerekiyor.
- **Kodda:** `src/FlowLens.Core/Ef/EfProbe.cs:164,175-179`. Çözüm:
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.
- **Kaynak:** `phase-3-notes.md §5 (ek)`

---

## 1.3 Sözdizimi ve keşif

### R15 — Minimal API endpoint'leri **`MethodDeclarationSyntax` değil** · **YÜKSEK**

- **Bilmiyorduk:** Hedefin **24 endpoint'inin 24'ünün** lambda olduğunu.
- **Nasıl çıktı:** Faz 1'in raporladığı **399 production metodunun hiçbiri bir endpoint değildi.**
  Ölçüm bunu gösterince Faz 2 baştan planlandı.
- **Doğrusu:** `async (...) => {...}` bir metot **bildirimi** değil, bir **ifade**
  (`ParenthesizedLambdaExpressionSyntax`). Endpoint = `MapGet`/`MapPost`/… çağrısı; gövde lambda
  argümanı.
- **Kodda:** `src/FlowLens.Core/EndpointDiscovery.cs:21-23,126` (`MapVerbs` sözlüğü) +
  `RoutePrefixResolver` (iki geçişli prefix çözümü).
- **Kaynak:** `known-limitations.md L1`, `phase-1-notes.md §6`

### R16 — `new X(...)` bir **invocation değil** — ctor gövdeleri hiç gezilmiyordu · **ORTA-YÜKSEK**

- **Bilmiyorduk:** `InvocationExpressionSyntax` takip eden bir yürüyücünün constructor'lara **hiç
  girmediğini**, ve bunun DDD bir aggregate'te kolonların çoğunu kaybettiğini.
- **Nasıl çıktı:** **Sessiz yanlış cevap.** `POST /api/catalog/products` **sıfır kolon**
  raporluyordu.
- **Doğrusu:** Bir aggregate'in kolonlarının çoğu ilk kez **ctor gövdesinde** yazılır. Bir metot
  `IModel`'deki bir tipi construct ediyorsa o tipin ctor'ları da analiz edilip kolon yazmaları
  **çağıran metoda** atfediliyor (ctor private, tek başına erişilemez).
- **Kodda:** `src/FlowLens.Core/Ef/EntityAccessAnalyzer.cs:239` (`BaseObjectCreationExpressionSyntax`),
  `:245-247` (yalnız `IsAttributableEntity` olan tipler — DTO gürültüsü dışarıda),
  `PropertyWriteAnalyzer.cs:215`. Sonuç: kolon **38 → 82**.
- **Kaynak:** `phase-3-notes.md §5.4`, `known-limitations.md L9`

### R17 — Endpoint lambda gövdeleri "yürüyüşün ulaştığı metot sembolleri" kümesinde **yok** · **ORTA-YÜKSEK**

- **Bilmiyorduk:** Metot sembolleri üzerinde çalışan bir overlay'in lambda gövdelerini **hiç
  görmediğini**.
- **Nasıl çıktı:** **Sessiz kayıp.** Dev endpoint'leri `DbContext`'i doğrudan lambda içinde
  kullanıyor (`context.Reservations…ExecuteUpdateAsync`) ve **hiçbir veri kenarı üretmiyorlardı** —
  "hiçbir şeye dokunmuyor" gibi görünüyorlardı.
- **Doğrusu:** Overlay'in lambda gövdelerini de gezmesi gerekiyor. 3 endpoint düzeltmeden sonra
  tablolarına ulaştı.
- **Kodda:** `GraphBuilder` overlay'i; `phase-3-notes.md §5.7c`.
- **Kaynak:** `phase-3-notes.md §5.7c`

### R18 — Aynı satırda iki çağrı: **kolon** gerekiyor, satır yetmiyor · **ORTA**

- **Nasıl çıktı:** Çağrı yeri sıralaması yazılırken `CreateProductHandler.cs:21`'de iki çağrı aynı
  satırda çıktı.
- **Doğrusu:** Çağrı yeri kimliği `(dosya, satır, **kolon**)`.
- **Kodda:** `SourceLocation.WithColumn`; `phase-5-notes.md §11.5`.

### R19 — "Dış çağrı" tespiti **yapısal** olmalı, isme bakarak değil · **ORTA**

- **Nasıl çıktı:** Aynı kod tabanında iki "dış sağlayıcı" soyutlaması var: `FakePspClient` ve
  `HttpEmbeddingService`. İsme bakan bir kural ikisini de dış çağrı sayardı.
- **Doğrusu:** Kural = çağrılan üyenin **declaring type**'ı `HttpClient`/`HttpMessageInvoker`.
  `FakePspClient` gövdesi `Task.Delay` + `Random.Shared.NextDouble()` — süreçten hiçbir şey
  çıkmıyor, node yok. Checkout için *"hangi dış servise gidiyor?"* sorusunun doğru cevabı:
  **hiçbirine.**
- **Kodda:** `src/FlowLens.Core/ExternalCallDetector.cs:22-23,37-40`;
  `ExternalCallsAreFoundByMechanismNotByName` testi sabitliyor.
- **Kaynak:** `phase-3-notes.md §4(D)`

### R20 — `GetCompilationAsync` + `GetDiagnostics()` **ucuz** · **ORTA**

- **Yanlış bildiğimiz:** 66 projeyi derlemek için "dakikalar" tahmin edilmişti.
- **Nasıl çıktı:** Ölçüm: **4,7 saniye**, 0 hata / 0 uyarı. Tahmin ~100× yanlıştı.
- **Doğrusu:** `GetCompilationAsync` derleyicinin **arka ucunu çalıştırmıyor** — syntax ağaçları +
  referanslardan `Compilation` nesnesini kuruyor; `GetDiagnostics()` binding yapıyor. Bu ölçekte
  ucuz.
- **Kaynak:** `phase-1-notes.md §2`

### R21 — `SymbolFinder` yalnız **interface** çağrılarında devreye giriyor · **ORTA**

- **Yanlış bildiğimiz:** Zincir yürüyüşünün pahalı olacağı (plan 30 s bütçe ayırmıştı).
- **Nasıl çıktı:** Ölçüm: **0,5 s** — tahminden ~40× hızlı. 369 invocation, **23 `SymbolFinder`
  çağrısı**, 6 cache isabeti.
- **Doğrusu:** Concrete çağrılar `SymbolFinder`'a hiç dokunmuyor; maliyet interface sayısıyla
  orantılı, invocation sayısıyla değil.
- **Kodda:** `src/FlowLens.Core/ImplementationResolver.cs:59,65,83`.
- **Kaynak:** `phase-2-notes.md §2`

---

## 1.4 Roslyn değil ama aynı aile — CLR ve çalışma zamanı

> Bu ikisi Roslyn değil; ama aracın doğruluğunu doğrudan belirledikleri ve **hiçbir tutorial'da
> geçmedikleri** için ayrıldı, atılmadı.

### R22 — JIT inlining **çerçeve düşürüyor**, async zincirler **bağışık** · **YÜKSEK**

- **Bilmiyorduk:** Bir yığın izindeki ardışık iki çerçevenin arasında **gerçekte bir çağrı daha**
  olabileceğini.
- **Nasıl çıktı:** **Ölçüm.** `A → C → B` zinciri, .NET 10.0.9 Release, üç yapılandırma:

  | Yapılandırma | sync `A→C→B` | kontrol `A→D[NoInlining]→B` | async `A→C→B` |
  |---|---|---|---|
  | Varsayılan (tiered on) | 3/3 | 3/3 | 3/3 |
  | `TieredCompilation=0` | **1/3** | 2/3 | **3/3** |

- **Doğrusu:** Üç çerçeveden ikisi silinebiliyor. **Sebebin gerçekten inlining olduğu kontrol
  grubuyla kanıtlandı**: `NoInlining` taşıyan `D` ayakta kaldı, aynı şekle sahip `C` kalmadı.
  Async zincirler bağışık — durum makinesinin `MoveNext`'i **gerçek bir fiziksel çerçeve**.
- **Ölçülen etki:** Hedefteki 255 metot düğümünün **97'si (%38) senkron**, yani düşürülebilir — ve
  tam da inline'a en uygun küçük domain yardımcıları (`Money.Add`, `Result.Failure`).
- **Kodda:** `graph'ta yok` hükmü ikiye ayrıldı; N ≥ 2 hop'luk yol varsa rapor *"atlanmış çerçeve
  olabilir"* der ve yolun düğümlerini yazar — **iddia değil gözlem**.
- **Kaynak:** `phase-6-notes.md §2`, `known-limitations.md L20`

### R23 — .NET 10 yığın izi biçimi: async **metot** demangle edilir, async **lambda** edilmez · **YÜKSEK**

- **Yanlış bildiğimiz:** *"Async metotlar demangle edilir"* doğru hatırlanmıştı; *"lambdalar da
  edilir"* **yanlış**. Hatırlananın yarısı yanlış çıktı.
- **Nasıl çıktı:** Parser yazılmadan **önce ölçüldü** (Faz 4'ün dersi: *"ölçmediğim şey hakkında
  konuştum"*).
- **Doğrusu:**

  | | Ölçülen |
  |---|---|
  | Async metot | `StrategyAsync` — demangle edilmiş |
  | Async lambda / local function | `<>c.<<RunAsync>b__0_0>d.MoveNext()` — **edilmemiş** |
  | Generic metot | `Inner[T](T value)` — köşeli parantez |
  | Generic tip | `Func\`1` — backtick arite |
  | **Parametre tipleri** | **CLR kısa adı**: `Int32`, `Single[]`, `Guid` |

- **Neden önemli:** Node id `float[], int, System.Threading.CancellationToken` yazıyor; yığın izi
  `Single[], Int32, CancellationToken`. `FrameMatcher`'ın CLR↔C# takma ad tablosu **yalnızca**
  bunun için var.
- **Kodda:** `src/FlowLens.Core/Triage/StackTraceParser.cs` (`Demangle`),
  `src/FlowLens.Core/Triage/FrameMatcher.cs` (takma ad tablosu).
- **Kaynak:** `phase-6-notes.md §1`

### R24 — xUnit paralel koleksiyonlar + iki `MSBuildWorkspace` = **kararsız suite** · **ORTA-YÜKSEK**

- **Bilmiyorduk:** Aynı solution'ı iki fixture'ın **eşzamanlı** açmasının çakışacağını.
- **Nasıl çıktı:** Bir koşu **12 hatayla** düştü, sonraki koşu tekrar yeşildi. Faz 2'nin testleri,
  **Faz 2'de olmayan bir sebeple** kırmızıya dönüyordu.
- **Doğrusu:** Aynı solution'ın iki yüklemesi MSBuild build-host süreçleri ve hedefin `obj/`
  dizini üzerinde çakışıyor; kaybeden taraf proje yükleme hatası raporluyor.
- **Kodda:** `[assembly: CollectionBehavior(DisableTestParallelization = true)]`.
  **Bedeli:** 31 s → ~85 s. **Gerekçesi:** kararsız bir suite yavaş olandan beterdir — insanlara
  kırmızının *"bir daha koş"* demek olduğunu öğretir.
- **Kaynak:** `phase-3-notes.md §8`

### R25 — `SymbolFinder.FindImplementationsAsync` **sabit sıra vaat etmiyor** · **YÜKSEK**

- **Bilmiyorduk:** Çıktının deterministik olmadığını — hiçbir yerde yazmıyor.
- **Nasıl çıktı:** Aynı kaynaktan iki ardışık build, **küme olarak birebir aynı** ama sırası farklı
  bir `graph.json` üretti: 8 node / 40 kenar yer değiştirmiş. Etkisi: **tek alanlık bir değişiklik
  `git diff`'te 216 satır**.
- **Doğrusu:** İmplementasyonların yürüme sırası node ekleme sırasını belirliyor. Düzeltme her
  üreticiye değil **tek çıkış noktasına** konuldu.
- **Kodda:** `src/FlowLens.Core/GraphJson.cs:109-127` (`Canonical`) — kenar anahtarı kenarın
  **taşıdığı her alanı** içeriyor, yani anahtarda eşitlenen iki kenar birebir aynı kayıttır.
  Ayrıca `elapsedMs` dosyadan çıkarıldı (makine hakkında bilgi, graph hakkında değil).
- **Kaynak:** `phase-4-notes.md §2`

---

# LİSTE 2 — Metodolojik bulgular (Roslyn'le ilgisiz)

## 2.1 Sessiz yanlış cevap ve testlerin kör noktası

### M1 — Dokuz hatanın dokuzu bir **YOKLUK**'tu; testlerin hepsi bir **VARLIK** iddia ediyordu · **YÜKSEK**

- **Ne oldu:** 110 test yeşilken `graph.json` üç yerde sessiz yanlış cevap veriyordu; doğrulamada
  yedi fark daha çıktı. Hepsi testler **geçerken** üretilmişti.
- **Ortak sebep:** Her test şu biçimdeydi — *"şu şekildeki koda karşı şu kenar üretilir."* Böyle
  bir test yalnız **modellenmiş** bir yol hakkında soru sorabilir. Interceptor'ın yazması, lambda
  gövdesi, owned navigasyon okuması, gölge kolon — bunlar üretim kodunun zihin haritasında yoktu,
  **aynı haritayla yazılan testlerde de yoktu**. Test suite'i ile üretim kodu **aynı kör noktayı
  paylaşıyordu**, çünkü aynı kişinin aynı modelinden çıkmışlardı.
- **Üç görünümü:** (a) örneklem vardı, **popülasyon yoktu** — 400 node'un tamamı üzerinde bir
  özellik iddia eden **tek bir test yoktu**; (b) round-trip simetrik hatayı göremez; (c) doğrulama
  tek yönlüydü — hiçbir assert *"bu neden yok?"* diye sormuyordu.
- **Üstü:** `graph.json` bu dört hatayla birlikte **kendi içinde tutarlıydı**. Dangling referans
  yok, 400/400 node konumlu, şema geçerli. **Tutarlılık, doğruluk değildir.**
- **Kaynak:** `phase-3-notes.md §10.1`

### M2 — Round-trip testi **simetrik hatayı göremez** · **YÜKSEK**

- **Ne oldu:** `RoundTripsNodesEdgesAndMechanisms` aynı serializer ile yazıp okuyordu. Yazarken
  düşen alan okurken de yoktu; eşitlik iddiası **geçiyordu**. 25 Endpoint node'u ve 512 CALLS
  kenarı `kind` alanı **olmadan** yazılıyordu.
- **Doğrusu:** Artefakt testi gibi görünen şey aslında bir **model** testiydi. Yeni test doğrudan
  **metne** bakıyor: `WritesKindEvenWhenItIsTheDefaultEnumValue`.
- **Yan ders:** `JsonIgnoreCondition.WhenWritingDefault` sıfır değerli her alanı düşürür — ve
  "alan yoksa varsayılan demektir" **yazılı olmayan bir kural** üretir; hata değil, **güvenle
  yanlış okuma**.
- **Kaynak:** `phase-3-notes.md §5.7a, §10.1b`

### M3 — Meşru olarak ihlal edilebilen invariant **ADI GEÇEN bir istisna** üretmeli · **YÜKSEK**

- **Kural:** *Sessizlik değil.* `GET /`'in orphan olması doğru; ama `build`'in *"1 orphan endpoint:
  GET /"* diye basması şart — **o satır 4'e çıktığı gün birileri görür.**
- **Ne oldu:** Denetimin bulduğu şeyi bulan aslında **bu satırın yokluğuydu.**
- **Uygulama:** İki seviye — **bloke edici** (graph diske yazılmaz: eksik `filePath`, dangling
  kenar, `kind` alanı, her `Column`'un tam bir `MapsTo` kenarı) ve **raporlayıcı** (yazılır ama
  çıktıda **adıyla** basılır: giden kenarı olmayan endpoint'ler, hiç kenar almayan tablolar).
- **Kaynak:** `phase-3-notes.md §10.4`

### M4 — İki hata, **tek satır üretim kodu yazılmadan**, bir varsayılanı seçmek için yapılan ölçümden · **YÜKSEK**

- **Ne oldu:** `includeUtility` varsayılanını seçmek için 25 endpoint forward + 16 tablo backward =
  **41 sorgu** koşuldu. İkisi de Faz 3 boyunca oradaydı ve **142 testin hiçbiri görmedi**.
- **Bulunanlar:** (1) bir **kök** yardımcı sayılıyordu (`RootKind != None ⇒ utility = false`
  invariant'ı eklendi); (2) filtre bir **erişilebilirlik** filtresiydi, sunum filtresi değil —
  utility node'un arkasında kalan utility olmayan her şey de düşüyordu.
- **Neden dar kontrol kaçırdı:** Filtre **tek bir tablo veya kolon kaybettirmiyordu** (48→48,
  244→244). Kaybolan **kök**'tü. **Yanlış sütuna bakan bir kontrol, sessiz kaybı doğrular gibi
  görünür.**
- **Kaynak:** `phase-4-notes.md §1`, `known-limitations.md L8`

---

## 2.2 Ölçüm kararı değiştirdi

### M5 — Ölçüm aracı, ölçülen şeyin **70 katı gürültü** üretebiliyor · **YÜKSEK**

- **Ne oldu:** İlk `/trace` ölçümü **138 ms** gösterdi ve `GraphSource`'un istek başına fazla iş
  yaptığını düşündürdü. Değilmiş — PowerShell'in `Invoke-WebRequest`'i 109 KB gövdeyi nesnelere
  çeviriyordu. `HttpClient` ile **1,91 ms**.
- **Kaynak:** `phase-4-notes.md §3`

### M6 — *"Düzeltildi"* dedim, düzeltmeyi **çalıştırmadım** · **YÜKSEK**

- **Ne oldu:** Beş ucun dördü 503 döndü. İlk teşhis *"content root yanlış"* oldu; çözüm yazıldı ve
  **test edilmeden** "düzeltildi" diye raporlandı. Ölçülünce görüldü ki `currentDirectory` ile
  `contentRoot` **aynıydı** — ikisi arasında seçim yapmak anlamsızdı, graph iki dizin yukarıdaydı.
- **Ders (kayıtlı hâliyle):** *"Ölçmediğim şey hakkında konuştum."* Aynı fazda ikinci kez.
- **Çözüm biçimi:** Sıralı arama + **denenen TÜM yolların** hata gövdesinde listelenmesi +
  `/graph/stats → graphFilePath` (hangi dosyanın okunduğu görünmüyordu) + açılışta koşulsuz log.
- **Kaynak:** `phase-4-notes.md §3`

### M7 — Beş tasarım kararı ölçümle **değişti** · **YÜKSEK**

| Karar | Önce | Ölçüm | Sonra |
|---|---|---|---|
| Subgraph ile modül kutulama | Q3'te makul ve **ikna edici** | 33 kenarın **12'si ilgisiz kutuyu kesiyor** | Reddedildi; modül etikete taşındı |
| Sabit yön (`LR`) | Tek yön tutarlı olur | 896 px'i aşan **20/25** | En geniş fan ≤ 7 → `TD` → **6/25** |
| Yön eşiği değişkeni | Node sayısı | 12 node'lu iki diyagram **zıt** cevap veriyor | **En geniş fan** — node sayısı yanlış değişkendi |
| "Sığmıyor" notu | Koşullu, vekil eşikle | Jeneratörün renderer'ı yok, piksel ölçülemez | Koşulsuz, **iddiasız** satır |
| Kardeş kenar sırası | Adım numarası `1..n` | 36 grubun **13'ü** aynı çağrı yerini paylaşıyor | Numara adım değil **çağrı yeri** sırası |

> **Kayda değer olan kararın kendisi değil, ölçümün kararı değiştirmesi.** Q3 tasarım aşamasında
> gerekçelendirilmiş, makul ve ikna edici bir karardı. Onu çürüten şey daha iyi bir argüman değil,
> **PNG'ye bakıp "bu çizgi neden Notification kutusunun içinden geçiyor" diye sormak** ve sonra o
> soruyu sayan bir metrik yazmak oldu. **Gerekçe, ölçümün yerine geçmez.**

- **Kaynak:** `phase-5-notes.md §3b, §9.4, §11.3`

### M8 — Sezgisel tarama ile kesin ölçüm: **seçim etkisi** · **ORTA-YÜKSEK**

- **Ne oldu:** Uygulamadan önceki sezgisel tarama *"18 çözülebilen grubun 18'i"* demişti; kesin
  ölçüm **36 grubun 13'ü (%36)**. Sezgisel yöntem grupların yalnız yarısını çözebiliyordu ve
  **çözebildikleri tam da paylaşımlı olanlardı**.
- **Not:** Karar değişmiyor, ama **sayı yanlıştı** ve düzeltmesi kayda geçti.
- **Kaynak:** `phase-5-notes.md §11.3`

### M9 — Vekil eşikle tahmin etmektense **iddiasız bir satır** yaz · **ORTA-YÜKSEK**

- **Ne oldu:** 6 dosyaya *"bu diyagram sığmıyor"* notu düşmek cazipti ve **yanlış olurdu**:
  jeneratörün renderer'ı yok, piksel ölçemez. Notu koşullu yapmak, hangi dosyanın sığmadığını bir
  vekil değişkenden (node sayısı, fan, etiket uzunluğu) **tahmin etmek** demekti — **ölçüm
  kılığında bir tahmin.**
- **Doğrusu:** Koşulsuz ama **iddiasız** cümle: *"Diyagram dar görünüyorsa tıklayarak
  büyütebilirsiniz."* 19 sayfada da doğru, 6 sayfada da doğru; determinizmi de bozmuyor.
- **Kaynak:** `phase-5-notes.md §9.6`

### M10 — Ölçüm yalnız **jeneratörün gerçekten ürettiği** metin üzerinde alınır · **ORTA**

- **Ne oldu:** Yerleşim denemeleri el yapımı bir `.mmd` üzerinde alınmıştı ve üretilen dosyadan
  saptı (demet çifti 11 yerine 15). Sebep: jeneratör node'ları id sırasına yazıyor, elle yapılan
  varyant gruplama sırasını koruyordu; dagre bildirim sırasına duyarlı.
- **Ders:** El yapımı varyant **yönü** doğru gösterir, **büyüklüğü** göstermez.
- **Kaynak:** `phase-5-notes.md §9.5`

---

## 2.3 Testin kendisini sınamak

### M11 — Mutasyon testi kırmıyorsa: **önce testi değil POPÜLASYONU sorgula** · **YÜKSEK**

- **Ne oldu:** Arayüz köprüsü 1 → 2 hop'a çıkarıldı, yani doğrulama kuralı **bilerek bozuldu**.
  **50 testin 50'si geçti.**
- **Sebep:** Testi doğru yazılmıştı; ama **beş fixture'ın hiçbirinde tam iki gerçek hop uzaklıkta
  bir çerçeve çifti yoktu.** Graph'ta bu şekilden **310 tane** var — ve aradaki düğümler senkron,
  yani inlining'in düşürdüğü %38'lik sınıfın ta kendisi.
- **Kural:** **Eksik testi fixture'dan değil GRAPH'tan seçerek yaz.** Fixture bir **örneklem**,
  graph **popülasyonun kendisi**; örneklemden test vakası seçmek, örneklemin zaten içerdiği
  şekilleri test etmektir — tanım gereği hiçbir boşluk bulamaz.
- **Kaynak:** `phase-6-notes.md §7a`

### M12 — Aynı yanılgının diğer yüzü: test **yanlış satırı** koruyordu · **YÜKSEK**

- **Ne oldu:** Faz 5'te komşuluk sıralaması mutasyona uğratıldı ve çıktı değişmedi — çünkü kardeş
  sırasını **çıkıştaki son sıralama** üretiyor. Komşuluk sıralaması yalnız tie-break'i etkiliyor.
- **İkisinin farkı:**

  | | Faz 5 | Faz 6 |
  |---|---|---|
  | Sebep | Test **yanlış satırı** koruyordu | Test doğru satırı koruyordu, **tetikleyecek veri yoktu** |
  | Soru | *"testim gerçekten neyi koruyor?"* | *"testimi tetikleyecek girdi elimde var mı?"* |
  | Düzeltme | Doğru satırı mutasyona uğrat | **Popülasyonu genişlet** |

- **Ortak:** Yeşil bir suite, kapsanmayan bir vakayı **kapsanmış gösterir**; ikisi de yalnız
  mutasyonla görünür oluyor — çünkü mutasyon, testin *var olduğunu* değil **işlediğini** sorgular.
- **Kaynak:** `phase-5-notes.md §11.6`, `phase-6-notes.md §7a`

### M13 — Sessizce atlanan test, sahip olmadığı kapsam için **yeşil** raporlar · **ORTA-YÜKSEK**

- **Kural:** Hedef solution bulunamazsa test **fail** eder, kurulum talimatlarıyla — atlanmaz.
  Doğrulandı: bozuk yolla 9 test fail, **0 atlandı**.
- **Uzantısı:** Faz 6'da bir test konusuz kaldı (`ASyntheticFixtureMatchesTheMeasuredFrameShape`)
  ve **bunu söyleyerek geçiyor**, atlanmıyor.
- **Kaynak:** `phase-1-notes.md §7`, `phase-6-notes.md §3`

### M14 — Testte sabit sayı yok; ölçülen sayılar **dokümanda** · **ORTA**

- **Gerekçe:** Hedef repo gelişmeye devam ediyor; yeni bir modül eklendiğinde test kırmamalı —
  bu gerçek bir regresyon değil. Testte çapa route'lar, tablo adları ve **invariant**'lar durur.
- **Kaynak:** `phase-1-notes.md §7`, `phase-2-notes.md §7`

### M15 — Kararsız bir suite, yavaş olandan **beterdir** · **ORTA**

- **Gerekçe (kayıtlı hâliyle):** İnsanlara kırmızının *"bir daha koş"* demek olduğunu öğretir.
  31 s → 85 s bedeli bilerek ödendi.
- **Kaynak:** `phase-3-notes.md §8`

---

## 2.4 Eval set'in kendisi ölçülen bir şeydir

### M16 — **Döngüsel oracle**: beklenen değeri tool'un kendi kuralından türetmek · **YÜKSEK**

- **Ne oldu:** Eval'in kolon kuralı (adım 7) ilk hâlinde **FlowLens'in kendi F3/L16 kuralının
  kopyasıydı**. Implementasyondaki bir hata iki tarafta da bulunur ve eval onu **göremez**.
- **Nasıl kırıldı:** Bağımsız otorite tektir — **EF'in gerçekten ürettiği SQL.** Gerçek Postgres 17
  container'ında, ModularCommerce'in derlenmiş DLL'leriyle, EF SQL logging açık, dört vaka.
- **Sonuç:** Kuralın **beş maddesi doğrulandı, biri çürütüldü** → `IdentityByDefault` kolonları
  INSERT'e girmiyor, EF onları `RETURNING` ile geri okuyor. **L21** açıldı.
- **Kaynak:** `phase-7-notes.md §1`

### M17 — Precision **%100** çıktı, ama **yanlış soruyla** ölçülmüştü · **YÜKSEK**

- **Ne oldu:** Faz 3 doğrulaması 15 kolonun her birini `Migrations/*.cs`'e karşı doğrulayıp
  *"15/15 gerçek, precision %100"* yazdı. İki negatif kontrol de tuttu.
- **Doğrusu:** Sorulan soru **"bu kolon migration'da var mı?"** idi → üçü de var. Sorulması gereken
  **"bu akış onu yazıyor mu?"** idi → üçünü de yazmıyor.
- **Aile:** Faz 6'nın *"test doğruydu, popülasyon sessizdi"* dersinin metrik seviyesindeki kardeşi:
  **metrik doğruydu, sorduğu soru yanlıştı.**
- **Kaynak:** `phase-7-notes.md §1`, `known-limitations.md L21`

### M18 — Eval set **kendi iç tutarlılığını** sınadı ve tutarsız çıktı · **YÜKSEK**

- **Ne oldu:** İlk koşuda *"öngörülmedi + gerçekleşti"* kutusunda tek soru vardı: Q19. Çapraz
  kontrol kaçırmanın **tool'da değil oracle'da** olduğunu gösterdi.
- **Asıl bulgu:** Kaçırmanın kendisi değil — **aynı köprü hakkında soru setinin iki farklı şey
  iddia etmesi.** Q01 checkout'un ileri cevabında o tabloyu bekliyor ve o iddia **doğrulandı**;
  Q19 ters yönde checkout'u beklemiyordu. İkisi aynı anda doğru olamaz.
- **Onu bulan:** FlowLens'in çıktısı değil, **eval set'in kendi içindeki çelişki**.
- **Sonraki adım:** Çelişki taraması **elle değil makineyle** yapıldı — 16 tablonun 6'sının iki
  yönü de soruluyor, çelişki tekti. Kalan **10 tablo çapraz kontrol edilemiyor**: tutarlı oldukları
  için değil, **sınanmadıkları** için sessizler.
- **Kaynak:** `phase-7-notes.md §2`

### M19 — Kapıyı **düzeltmeden önce** yazmak, bir yerine **iki** hata buldu · **YÜKSEK**

- **Ne oldu:** Q06 *"öngörüldü, gerçekleşmedi"* kutusuna düştü — rapor **öngörünün yanlış
  olduğunu** söylüyordu. Değildi: soru `externalStores`'u hiç **iddia etmiyordu**, yani cevapta
  oynayabilecek eksen yoktu. Öngörü yanlış değil, **ölçülemezdi**. İkisi farklı sonuç ve 3×2 onları
  aynı kutuda gösteriyordu.
- **Kapı:** `EveryPredictedFailureHasAnAxisThatCouldRealiseIt` — düzeltmeden **önce** yazıldı ve
  22 sorunun tamamını taradı. Sonuç: **2 soru, 4 girdi** — Q06 **ve Q01**.
- **Kural:** *Eksik kapıyı bilinen vakadan değil, **popülasyonun tamamından** türet.*
- **Kaynak:** `phase-7-notes.md §3`

### M20 — Popülasyon sayımı **tanıma** duyarlıdır — ve beklenen değerin kendisine de · **YÜKSEK**

- **Ne oldu (1):** F7 sınıfının popülasyonu ilk operasyonelleştirmeyle **0** ölçüldü. Bu *"sınıf
  kapandı"* demek **değildi**; **tanım** yanlıştı. Doğru tanımla **4** çıktı.
- **Ne oldu (2):** Q19 *"kök kümesi yalnız Consumer olan tek tablo"* diyordu, `count: 1`. Ölçüm:
  öyle bir tablo **sıfır**. Yanlış bir `expected`, yanlış bir popülasyon tanımı üretmişti.
- **Ders:** *"0 örnek"* iki şey demek olabilir — sınıf kapandı, ya da **tanım sınıfı ıskalıyor**.
  İkisini ayırmadan "kapandı" yazmak sessiz hatanın kendisidir.
- **Kaynak:** Faz 7 planı, `phase-7-notes.md §2`

### M21 — Ölçüm aracının hata payı **rapordaki bir satır** · **ORTA-YÜKSEK**

- **Ne oldu:** 13 kaçırmanın hepsi kaynağa karşı çapraz kontrol edildi: **13 `oracle-doğrulandı`,
  0 `oracle-düzeltildi`** — bu turda. Önceki turda **3 düzeltme** vardı ve üçü de ayrı commit'te,
  her biri düzeltmeyi **çürüten kaynak satırını** mesajında taşıyor.
- **Mekanizma:** Verdict'ler `questions.json`'a değil **ayrı bir dosyaya** yazılıyor; düzeltmeler
  runner commit'ine karışmıyor. Böylece *"beklenen değer çıktıya uydurulmuş mu?"* sorusu **tek bir
  `git log`** ile cevaplanıyor.
- **Kaynak:** `evals/oracle-verdicts.json`, `phase-7-notes.md §3`

### M22 — Kutu tablosu doğru okunsa bile **kalem düzeyindeki bulguyu gizleyebilir** · **ORTA-YÜKSEK**

- **Ne oldu:** L23 (owned koleksiyon içindeki owned tipin kolonları) Q01'de **soru düzeyinde**
  "öngörüldü + gerçekleşti" kutusundaydı; ama kaybolan iki kolon Q01'in **yedi öngörüsünün
  hiçbirine** girmiyordu. Kutunun birimi soru, kalem değil.
- **Onu açığa çıkaran:** Kutular değil, **soru soru okumak**.
- **Kaynak:** `evals/report.md §5`, `known-limitations.md L23`

### M23 — Ölçülmeyen bir şeyi ölçülmüş göstermemek · **ORTA**

- **Ne oldu:** Sınır kodları bir **varlık** iddiası olduğu için precision hesaplanmıyor; yani hak
  edilmeden takılan bir uyarı **hiçbir sayıya yansımıyor**. Böyle bir örnek var ve **elle** bulundu
  (`inventory.reservations`, L24).
- **Karar:** Rapora *"bu ölçülmüyor"* satırı kondu — kapatılmadı, **ilan edildi**.
- **Kaynak:** `evals/report.md §2`

---

## 2.5 Fixture ve gerçeklik

### M24 — "Gerçek fixture" iddiası bir **merdivenle** karşılanır, sözle değil · **YÜKSEK**

- **Ne oldu:** Plan *"üç gerçek yığın izi"* diyordu ama **nasıl üretileceğini yazmıyordu.** Elle
  yazılmış bir yığın izi ölçülen biçimi **taklit eder** — yani parser'ı, test etmesi gereken şeye
  karşı değil **kendi varsayımına** karşı test eder.
- **Çözüm:** Dört basamaklı bir düşüş kuruldu ve **inilen basamak her fixture dosyasının başında
  yazılı**. Sonuç: **5 gerçek, 0 sentetik** — gerçek Postgres/pgvector container'ları üzerinden,
  hedef repoya **tek bayt yazılmadan**.
- **Kapı:** `EveryFixtureDeclaresWhereItCameFrom` + `AtLeastTwoFixturesAreRealCaptures` — biri elle
  yazılmış bir izle değiştirilirse kriter **sessizce düşmez**.
- **Kaynak:** `phase-6-notes.md §3`

### M25 — İstenen satırı elde etmek için izi **düzenlemek**, sentetiği gerçek diye sunmaktır · **YÜKSEK**

- **Ne oldu:** A fixture'ı istenen `:37` yerine `:16`'ya düştü — EF'in kendi SELECT'i de aynı
  kolonu adlandırdığı için akış ham SQL'e hiç varmadan kırılıyordu.
- **Yapılmayan:** İzi düzenlemek.
- **Yapılan:** Ariza **mekanizması** değiştirildi (audit trigger) **ve ilk deneme de fixture olarak
  tutuldu**. Sonuç: A tam-satır isabetini, A2 aynı dosyada satırın **tutmadığı** negatif vakayı
  gösteriyor. Rapor 4 fixture'ın 2'sinde tam-satır isabeti olmadığını **iddia etmiyor**.
- **Kaynak:** `phase-6-notes.md §3`

### M26 — Sonradan eklenen fixture: ilk dördü **köprüyü hiç çalıştırmıyordu** · **ORTA-YÜKSEK**

- **Ne oldu:** İlk dört yakalamada strateji **doğrudan** çağrılıyordu, yani arayüz köprüsü hiç
  devreye girmiyordu. D, arizaya **üretimin ulaştığı yoldan** gidiyor.
- **Ders:** Bir fixture setinin "gerçek" olması, **kapsayıcı** olduğu anlamına gelmiyor.
- **Kaynak:** `phase-6-notes.md §3`

---

## 2.6 Determinizm ve çıktı disiplini

### M27 — Sıralamayı deterministik yapmak **yetmez**; keşfin kendisi deterministik olmalı · **YÜKSEK**

- **İki katmanlı ders:**
  - **Faz 4:** küme doğruydu, **yazılış sırası** değişkendi → `GraphJson.Canonical`.
  - **Faz 5:** **üretilen kümenin kendisi** farklıydı. `seen` kümesi bir düğüme hangi kenarın
    bağlanacağını keşif sırasına bırakıyordu; iki koşu iki farklı **kenar kümesi** üretiyordu.
- **Kritik cümle:** *Çıktıyı sıralamak bunu **gizlerdi, düzeltmezdi.** Sıralı ama yanlış bir
  diyagram hâlâ deterministik görünür — ve non-determinizmi arayan tek test (byte karşılaştırması)
  artık **yeşil** olurdu.*
- **Test biçimi:** İki koşuyu değil **permütasyonu** karşılaştır — aynı graph ters sırada
  verildiğinde de aynı baytlar. *"İki koşu tuttu"*dan güçlü bir iddia ve ikinci bir 32 saniyelik
  build gerektirmiyor.
- **Kaynak:** `phase-4-notes.md §2`, `phase-5-notes.md §2, §7`

### M28 — Encoder determinizmi: format belirlenmiş olabilir, **çıktısı encoder'a bağlıdır** · **ORTA**

- **Ne oldu:** mermaid.live bağlantıları `base64url(zlib(json))` taşıyor. `ZLibStream` **geçerli**
  bir akış üretirdi ama **yeniden üretilebilir** bir akış üretmezdi — çıkardığı baytlar zlib
  derlemesine bağlı (.NET 8'den beri zlib-ng). Aynı graph iki makinede **iki farklı bağlantı**
  verir ve 25 sayfa **sahte diff** gösterirdi.
- **Çözüm:** **Stored block** elle yazıldı (`BTYPE=00`); RFC 1950/1951 bunu tam belirliyor,
  encoder'a bırakılmış seçim yok. **Bedeli ölçüldü:** en kötü vakada 2,84× (2678 karakter).
- **Ek tuzak:** Gövde bağlantıya girmeden `\n`'e **normalize** ediliyor — `AppendLine`
  `Environment.NewLine` yazıyor.
- **Kaynak:** `phase-5-notes.md §9.6`

### M29 — Markdown **lazy continuation**: parse edilir, **yanlış render olur** · **ORTA-YÜKSEK**

- **Ne oldu:** **10 modül sayfasının 10'unda** madde listesi ile onu izleyen kalın başlık arasında
  boş satır yoktu — başlık son maddenin **içine** giriyordu. Sebep tek bir eksik `AppendLine`.
- **Kim görmedi:** Ne derleyici, ne 194 test, ne `mermaid.parse()`. **Dosyaları okurken bulundu.**
- **Asıl ders:** Kural bir **sınıftı**, tek satır değil. Aynı kapı bir sonraki fazın markdown'ına
  taşındı ve **ilk koşuda düştü** — kusur üretilir üretilmez yakalandı.
  > *Kuralı bir fazda öğrenip bir sonrakinde uygulamamak, kuralı hiç öğrenmemekle aynı sonucu
  > verir.*
- **Kaynak:** `phase-5-notes.md §5.3`, `phase-6-notes.md §7b`

### M30 — Üretilmiş kodu **kaynak sanmak** — iki kez, iki farklı yerde · **ORTA**

- **(a)** Tablolar üretilmiş migration koduna atfediliyordu (`…Designer.cs:39`), çünkü
  migration'lar modelin tamamını yeniden bildiriyor. **`filePath` + `line`'ın tüm vaadi oraya
  gidip tarif edilen şeyi değiştirebilmek**; üretilmiş bir snapshot'a yönlendirmek okuyucuyu
  **düzenlememesi gereken** bir dosyaya gönderir.
- **(b)** Bayatlık dedektörü `obj/` altındaki üretilmiş `AssemblyInfo.cs`'leri sayıyordu, yani her
  build bayat görünüyordu.
- **Kodda:** `/Migrations/`, `*.Designer.cs`, `*ModelSnapshot.cs` taramadan çıkarıldı
  (`TablesAreAttributedToConfigurationsNotToGeneratedMigrations`); `obj/`+`bin/` hariç tutuldu.
- **Kaynak:** `phase-3-notes.md §5.6` ve `§5 (ek)`

### M31 — Rapor kusuru da bir **bulgu**dur · **ORTA**

- **Ne oldu:** `tablo (erişim R/W)` satırı popülasyon olarak **uyuşmazlıkları** kullanıyordu:
  37 kontrolün 6 uyuşmazlığı `6 beklenen · 0 bulunan · %0` diye okunuyordu — ölçülenden çok daha
  kötü bir sayı ve uyuşan 31 hakkında hiçbir şey söylemiyor.
- **Karar:** Sessizce düzeltilmedi, **ayrı commit'e** kondu.
- **Kaynak:** `phase-7-notes.md §3`

---

## 2.7 Kullanıcı arayüzü ve yanlış okuma

### M32 — Yanlış okunan bir komutu **yardım metniyle** düzeltemezsin · **ORTA**

- **Ne oldu:** İki komutun adı da `trace`; hangisinin veri katmanını verdiği **iki kez yanlış
  okundu**.
- **Çözüm biçimi:** Yardım metnini iyileştirmek yetmedi. **Koşan komutun kendisi** kendini
  etiketliyor: *"note: this is the live call-chain walk — NO tables or columns"* + doğru komut,
  hedefin **kendi yoluyla** birlikte.
  > *Yanlış komutu çalıştıran kişi yardım metnini zaten okumamıştır.*
- **Kaynak:** `phase-3-notes.md §6.5`

### M33 — Yetenek vardı, **keşfedilebilirlik** yoktu · **ORTA**

- **Ne oldu:** İlk sürümde `--graph` zorunluydu; `flowlens trace "POST /api/ordering/checkout"`
  hata veriyordu ve kullanıcı tablosuz Faz 2 çıktısına düşüyordu. **Kabul kriterinin harfi
  karşılanmıyordu** — kriter birebir `flowlens trace <endpoint>` diyordu.
- **Kaynak:** `phase-3-notes.md §7`

---

## 2.8 Aracın hedef repo hakkında bulduğu şeyler

### M34 — Beş gözlem, hiçbiri aracın hatası değil · **ORTA-YÜKSEK**

| # | Bulgu | Nerede |
|---|---|---|
| 1 | **Survey 68/66** — solution'da bildirilenden fazla proje; hata **oracle'daydı**, tool'da değil | Faz 1 |
| 2 | **Bayat build** — hedef, en yeni kaynak dosyasından önce derlenmiş | Faz 3 |
| 3 | **`OrderCancelled` yayınlanıyor ama tüketicisi yok** | Faz 3 (diagnostics) |
| 4 | **Shared'daki bir background service modül tablolarına yazıyor** | Faz 4 (utility ölçümü) |
| 5 | **Kod kendini yanlış anlatıyor** — yorum var olmayan bir tüketiciyi tarif ediyor | Faz 7 (eval oracle'ı) |

- **3 ve 5 birlikte daha keskin:** Faz 3 **diagnostics'ten** *"yayınlanıyor, tüketicisi yok"* dedi;
  Faz 7 **yorumdan** *"tüketicisi var yazıyor"* buldu. Eksiklik yalnız kodda değil, kodun
  **kendisi hakkında söylediğinde**.
  > **Yorum bir kanıt değil.** Kanıt zinciri **bildirime** bakar, açıklamaya değil.
- **4 hakkında:** Araç *"ihlal"* demiyor, *"Contracts dışından doğrudan referans"* diyor ve
  `file:line` veriyor. **Araç kuralı uygular, hüküm vermez.**
- **Kaynak:** `phase-4-notes.md §4`

---

# Anlatı için üç aday çerçeve

1. **"Yeşil testler ne söylemez"** — M1 → M2 → M4 → M11/M12 → M17. Beş fazda aynı hatanın beş
   farklı yüzü; her biri bir öncekinin bir katman derinini gösteriyor.

2. **"Ölçüm kararı değiştirdiğinde"** — M7 merkezde, M5/M6/M8/M9/M10 yanlarında. Tezi: *gerekçe,
   ölçümün yerine geçmez* — ve en iyi örneği, iyi gerekçelendirilmiş bir kararın (subgraph) bir
   PNG'ye bakılarak çürütülmesi.

3. **"Ölçüm aracının kendisi ölçülür"** — M16 → M18 → M19 → M21 → M23. Eval set'in kendi hata
   payını raporun ilk tablosuna koyması; ve bir soru setinin **kendi içindeki çelişkiyi** bulması.

Roslyn tarafında en güçlü açılış adayları: **R7** (JIT metot sınırı), **R1** (compilation başına
sembol kimliği), **R25** (`SymbolFinder` sıra vaat etmiyor) — üçü de dokümante edilmemiş,
üçü de **sessiz** yanlış cevap üretiyor.
