# Faz 1 — Roslyn'e ısınma (tamamlandı)

> Ölçüm tarihi: 2026-08-07 · Hedef: `ModularCommerce.sln` (66 proje) · SDK 10.0.301

---

## 1. Kabul kriterleri

| Roadmap kriteri | Durum | Kanıt |
|---|---|---|
| Solution hatasız yükleniyor | ✅ | `Loaded 66/66 projects`, `Workspace diagnostics: Clean` |
| Metot sayısı konsola basılıyor | ✅ | 773 method declaration / 598 doküman |
| Modül başına metot sayısı raporlanıyor | ✅ | modül × katman matrisi (aşağıda) |
| Yüklenemeyen proje `WorkspaceFailed` ile loglanıyor | ✅ | `SolutionLoader` + 4 bağımsız sinyal (§4) |
| En az bir integration test | ✅ | 24 test, tamamı yeşil |

---

## 2. Ölçülen sayılar

```
Loaded 66/66 projects in 17,3s
773 method declarations in 598 documents (0,9s)
```

| Modül | Api | Application | Domain | Infrastructure | Contracts | (other) | Toplam |
|---|---:|---:|---:|---:|---:|---:|---:|
| Cart | 3 | 8 | 9 | 22 | 2 | · | 44 |
| Catalog | 3 | 6 | 10 | 40 | 1 | · | 60 |
| Discovery | 8 | 8 | 1 | 14 | · | · | 31 |
| Identity | 3 | 9 | 14 | 19 | · | · | 45 |
| Inventory | 4 | 8 | 18 | 32 | 5 | · | 67 |
| Notification | 5 | 2 | · | 11 | · | · | 18 |
| Ordering | 3 | 9 | 15 | 26 | 1 | · | 54 |
| Payment | 3 | 4 | 7 | 20 | 2 | · | 36 |
| Shared | · | · | · | 28 | · | 14 | 42 |
| Shipping | 2 | · | · | · | · | · | 2 |
| **Production toplam** | | | | | | | **399** |

Test projeleri ayrı raporlanıyor: **374 metot** — Inventory 73, Ordering 63, Cart 52,
Identity 47, Catalog 43, Payment 36, Discovery 19, Notification 14, Shared 14, (unknown) 13.
`--include-tests` ile tabloya katılır.

**Süreler** (tek makine, ölçüm):

| İşlem | Süre |
|---|---|
| Solution yükleme (66 proje, design-time build) | 17–20 s |
| Syntax taraması (598 doküman) | 0,9 s |
| Tüm projelerin derlenmesi (`--check-compilation`) | 4,7 s → 0 hata, 0 uyarı |
| Test paketi (24 test, yükleme dahil) | 20 s |

> **Plandaki tahminimi düzeltiyorum:** `--check-compilation` için "dakikalar" demiştim; gerçek
> 4,7 saniye. `GetCompilationAsync` derleyicinin arka ucunu çalıştırmıyor — sadece syntax
> ağaçları + referanslardan `Compilation` nesnesini kuruyor, `GetDiagnostics()` de binding
> yapıyor. Bu ölçekte ucuz. Flag yine de yerinde: 0 hata/0 uyarı çıktısı, hiçbir şey yapmayan
> bir kontrolden ayırt edilemez göründüğü için `CompilationCheckerTests` bunu ayrıca
> kanıtlıyor (CS0103 yakalanıyor, CS0219 uyarı olarak sayılıyor).

**Shipping = 2 metot** doğru: modül bilinçli olarak boş kabuk, sadece `Register` +
`MapEndpoints` var (ikisi de boş gövde).

---

## 3. `MSBuildLocator` tuzağı — asıl kural

Roadmap "ilk satırda çağrılmalı" diyor. **Bu yetersiz bir formülasyon; sorun satır sırası değil,
JIT zamanlaması.**

CLR bir metodu ilk çağrıldığında derler ve derlerken **gövdesinde adı geçen her tipin**
assembly'sini resolve eder. Yani şu kod, `RegisterDefaults` ilk satır olmasına rağmen patlar:

```csharp
static async Task Main(string[] args)
{
    MSBuildLocator.RegisterDefaults();     // çalışma fırsatı bulamaz
    var ws = MSBuildWorkspace.Create();    // tip, Main derlenirken resolve edilir
}
```

`Main`'e girildiği anda `MSBuildWorkspace` resolve edilir → `Microsoft.CodeAnalysis.Workspaces.MSBuild`
yüklenir → o da `Microsoft.Build.*` ister. Bu assembly'ler NuGet paketinde değil, .NET SDK
klasöründe. Onları bulacak `AssemblyResolve` handler'ını kuran tek şey `RegisterDefaults()` —
ve o satır henüz **çalışmamıştır**. Sonuç: `FileNotFoundException: Microsoft.Build`.

**Doğru kural: ayrım metot sınırıyla yapılır, satırla değil.**

```
Program.cs
  ├─ MSBuildLocator.RegisterDefaults()   ← yalnız Microsoft.Build.Locator yüklenir (güvenli)
  └─ await Runner.RunAsync(args)          ← Runner TİPİ resolve edilir; GÖVDESİ henüz değil
```

`Runner.RunAsync` ilk çağrıldığında JIT edilir — o an handler kurulmuştur.
`[MethodImpl(MethodImplOptions.NoInlining)]` şart: inline edilirse gövde `Main`'in JIT'ine
karışır ve aynı tuzağa düşülür. Uygulama: [Program.cs](../src/FlowLens.Cli/Program.cs),
[Runner.cs](../src/FlowLens.Cli/Runner.cs).

Test tarafında aynı kural `[ModuleInitializer]` ile karşılanıyor
([TestModuleInitializer.cs](../tests/FlowLens.Tests/TestModuleInitializer.cs)) — module
initializer, assembly'deki hiçbir metot çalışmadan önce koşar.

### MSBL001 — beklemediğim ikinci tuzak

İlk build şu hatayla düştü:

```
error MSBL001: A PackageReference to the package 'Microsoft.Build.Framework' at version
'17.11.48' is present in this project without ExcludeAssets="runtime" and PrivateAssets="all"
```

`Microsoft.CodeAnalysis.Workspaces.MSBuild`, `Microsoft.Build.Framework`'ü transitive olarak
getiriyor. O assembly `bin/`'e kopyalanırsa runtime'da **iki kopya** olur ve MSBuildLocator'ın
resolver'ı devre dışı kalır — yani tam olarak locator'ın engellemek için var olduğu hata.
Çözüm [Directory.Build.props](../Directory.Build.props)'ta: `ExcludeAssets="runtime"` +
`PrivateAssets="all"`, sürüm transitive olanla birebir pinlenmiş (NU1605 önlemi).

**Ayrıca:** `MSBuildWorkspace`, `Microsoft.CodeAnalysis.CSharp.Workspaces` paketinde **değil**.
Ayrı paket: `Microsoft.CodeAnalysis.Workspaces.MSBuild` (5.6.0). Roadmap'in paket listesinde
eksikti, eklendi.

---

## 4. Yükleme hatalarını yakalama — dört bağımsız sinyal

Asıl tuzak: **`OpenSolutionAsync` bir proje yüklenemezse exception atmaz.** Sessizce atlar ve
eksik bir `Solution` döner. Fark edilmezse Faz 2'de "bu metot hiç çağrılmıyor" gibi *güvenle
yanlış* sonuçlar üretilir.

| # | Sinyal | Uygulama | Neyi yakalar |
|---|---|---|---|
| 1 | `RegisterWorkspaceFailedHandler` | `SolutionLoader.cs` | Yüklenemeyen proje, eksik restore, bilinmeyen SDK |
| 2 | `.sln`'den sayılan proje ↔ yüklenen proje | `CountProjectsInSolutionFile` | Sessizce atlanan projeler — event kaçsa bile yakalar |
| 3 | `project.SupportsCompilation` | `MethodScanner` | Analiz edilemeyen projeler (`SkippedProjects`) |
| 4 | `compilation.GetDiagnostics()` | `--check-compilation` | Yüklendi ama derlenmiyor → SemanticModel güvenilmez |

**Handler `OpenSolutionAsync`'ten ÖNCE bağlanmalı** — replay buffer'ı yok, sonra bağlanan hiçbir
şey görmez ve yükleme temiz görünür.

Diagnostic'ler `(Kind, Message)` üzerinden dedupe ediliyor: MSBuildWorkspace aynı mesajı etkilenen
her proje için ayrı ayrı fırlatıyor, ham akış okunamaz oluyor. Tekrar sayısı korunuyor.

**Not:** `Workspace.WorkspaceFailed` event'i artık obsolete (CS0618); yerine
`RegisterWorkspaceFailedHandler` kullanıldı — `IDisposable` bir kayıt döndürüyor.

**Exit code'lar:** `0` temiz · `1` yükleme problemi (failure veya proje sayısı uyuşmazlığı) ·
`2` yüklendi ama derleme hatası var · `64` kullanım hatası.

---

## 5. SyntaxTree vs SemanticModel — ölçülen fark

`CheckoutHandler.HandleAsync` üzerinde (`--check-compilation` gerektirmez, her çalıştırmada
görünür):

```
        return : Task<Result<CheckoutResponse>>                      ← SyntaxTree
        return : System.Threading.Tasks.Task<
                   ModularCommerce.Shared.Kernel.Result<
                     ModularCommerce.Ordering.Application.Orders.Checkout.CheckoutResponse>>   ← SemanticModel
```

```
syntax   : orders.GetByIdempotencyKeyAsync
semantic : ModularCommerce.Ordering.Domain.Orders.IOrderRepository
             .GetByIdempotencyKeyAsync(System.Guid, string, System.Threading.CancellationToken)
assembly : ModularCommerce.Ordering.Domain  <- INTERFACE member
```

`SyntaxTree` için `orders.GetByIdempotencyKeyAsync` yalnızca **iki string ve bir nokta**.
`orders`'ın ne olduğunu, metodun hangi assembly'de yaşadığını, hatta gerçekten var olup
olmadığını bilemez. `SemanticModel` aynı düğümü bir `IMethodSymbol`'e bağlıyor. Faz 2'nin
tamamı bu bağlamanın üstünde duruyor.

**Sayısal sonuç:** `HandleAsync` gövdesindeki **49 invocation'ın 49'u** temiz çözüldü;
**10'u bir interface üyesine** bağlandı.

### İki uygulama notu

- `SymbolDisplayFormat.FullyQualifiedFormat` **tipler için** tasarlanmış; bir metot sembolünde
  içeren tipi atlıyor ve ilginç kısım `ValidateAsync` diye görünüyor. Metotlar için
  `IncludeContainingType | IncludeParameters` taşıyan ayrı bir format gerekiyor
  (`SemanticModelDemo.MemberFormat`).
- `semanticModel.GetDeclaredSymbol(...)` taban sınıftan çağrılırsa `ISymbol` döner.
  `IMethodSymbol` almak için `using Microsoft.CodeAnalysis.CSharp;` (CSharpExtensions) gerekir.

---

## 6. Faz 2'ye taşınan bulgu

**En kritik olan ayrı dosyada:** [known-limitations.md](known-limitations.md) → **L1**.

Özet: ModularCommerce'in **24 endpoint'inin 24'ü** Minimal API lambda'sı.
`MethodDeclarationSyntax` bunları yakalamıyor, dolayısıyla yukarıdaki 399 production metodunun
**hiçbiri bir endpoint değil**. Call graph'ın giriş noktalarının tamamı bu demek — Faz 2
`ParenthesizedLambdaExpressionSyntax` desteği olmadan başlayamaz.

Diğer taşınanlar aynı dosyada: L2 (diğer bildirim biçimleri), L3 (interface belirsizliği,
ölçüldü: 10/49), L4 (generic `Publish<T>` yok), L5 (design-time factory yok), L6 (statik
analizin yapısal sınırları).

---

## 7. Test stratejisi

24 test, 20 saniye. İki grup:

**Unit (hızlı, hedef repo gerektirmez)** — `ProjectClassifierTests` (yol → modül eşlemesi),
`CompilationCheckerTests` (`AdhocWorkspace` ile, MSBuild'siz).

**Integration** — `SolutionLoaderIntegrationTests`, gerçek solution'a karşı.

İki bilinçli karar:

1. **Sessiz geçme yok.** Hedef solution bulunamazsa test **fail** eder, kurulum talimatlarıyla.
   Fixture'ı olmayınca kendini atlayan test yeşil görünüp hiçbir şey doğrulamaz — koruduğu şey
   bozulduktan sonra da paket geçmeye devam eder. Yol sırası:
   `FLOWLENS_TARGET_SLN` env değişkeni → `appsettings.test.json`.
   Doğrulandı: bozuk yolla 9 test fail, 0 atlandı.

2. **Sabit sayı yok.** `66` veya `9` gibi literaller yerine:
   - yüklenen proje sayısı **==** `.sln`'den runtime'da sayılan proje sayısı
   - modül sayısı > 0 **ve** çapa modüller (`Ordering`, `Catalog`, `Inventory`) listede
   - `HasFailures == false`

   ModularCommerce gelişmeye devam ediyor; yeni bir modül eklendiğinde test kırmamalı — bu
   gerçek bir regresyon değil. Günün kesin sayıları §2'de, testte değil.

---

## 8. Kapsam dışı bırakılanlar

Faz 1'de bilerek yapılmayanlar: graph/node/edge modeli, JSON çıktı, `SymbolFinder`,
EF Core `IModel`, lambda desteği. Hepsi Faz 2–3.
