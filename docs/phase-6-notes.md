# Faz 6 — Triage Bot: notlar

LLM yok, solution yüklenmiyor, git'e yazılmıyor. Girdi bir yığın izi; çıktı bir rapor.

```
flowlens triage --stack-trace <dosya|->  [--repo <yol>] [--graph <yol>] [--json] [-o <dosya>]
flowlens triage --method "<Tip.Metot>"   [--exception <Tip>]
```

Bu faz **yeni bir doğruluk kaynağı kurmadı.** Kökler ve tablolar Faz 4'ün `AnswerBuilder`'ından
geliyor — HTTP API'nin ve Faz 5'in dokümantasyon üreticisinin çağırdığı aynı kod. Eklenen tek şey
girdi: soruyu sormak için node id'si bilmek gerekiyordu, incident'ta elde olan ise bir yığın izi.

---

## 1. Adım 0a — .NET 10 çerçeve biçimi, ölçüldü

Parser **hatırlanan** biçime göre değil, ölçülen metne göre yazıldı. Faz 4'ün dersi buydu:
*"ölçmediğim şey hakkında konuştum."* Ölçüm .NET 10.0.9, Release, scratchpad'de tek dosyalık bir
program; repoya hiçbir şey girmedi.

```
   at Step0.AsyncChain.StrategyAsync(Guid productId, Int32 quantity, CancellationToken cancellationToken) in ...\Program.cs:line 28
   at Step0.LambdaAndGeneric.Inner[T](T value) in ...\Program.cs:line 44
   at Step0.LambdaAndGeneric.<>c.<<RunAsync>b__0_0>d.MoveNext() in ...\Program.cs:line 36
--- End of stack trace from previous location ---
```

| Bulgu | Ölçülen |
|---|---|
| Async **metot** | **Demangle edilmiş** — `StrategyAsync`, `MoveNext` yok |
| Async **lambda / local function** | **Demangle EDİLMEMİŞ** — `<>c.<<RunAsync>b__0_0>d.MoveNext()` |
| Generic metot | `Inner[T](T value)` — köşeli parantez |
| Generic tip | `Func\`1`, `IEnumerable\`1` — backtick arite |
| **Parametre tipleri** | **CLR kısa adı**: `Int32`, `Guid`, `Single[]`, `Boolean` |
| Parametre adları | var |
| Konum | `in <mutlak PDB yolu>:line <n>` |

**Hatırladığımın yarısı yanlıştı.** Async metotların demangle edildiğini doğru hatırlamışım; async
lambdaların da edildiğini yanlış. `MoveNext` işleme yolu bu yüzden hâlâ taşıyıcı.

Parametre biçimi eşleştirmeyi doğrudan etkiliyor: node id `float[], int,
System.Threading.CancellationToken` yazıyor, yığın izi `Single[], Int32, CancellationToken`.
`FrameMatcher`'ın CLR↔C# takma ad tablosu **yalnızca** bunun için var.

### Gerçek yakalamalarda çıkan iki şey daha — sentetikte yoktu

1. **Aynı metot arka arkaya iki kez, farklı satırla.** `ProductVectorRepository.SearchAsync`
   `:65` ve `:71`'de görünüyor: `await using` dispose sırasında yeniden fırlatıyor. Doğrulama
   tablosunda **`aynı metot (kenar beklenmiyor)`** hükmü bu yüzden var — bir metottan kendine kenar
   yok, ve `graph'ta yok` demek okuyucuya graph'ta bir boşluk varmış gibi gelirdi.
2. **Framework çerçeveleri tekrarlanıyor.** `NpgsqlDataReader.NextResult` iki kez,
   `RelationalCommand.ExecuteNonQueryAsync` üç kez. Bu yüzden rapor **iki sayı** taşıyor:
   `12 çerçeve (8 farklı metot)`. Tekini yazmak 14 satırlık bir izi ya şişkin ya cılız gösterirdi.

---

## 2. Adım 0b — ⚠ Inlining çerçeve DÜŞÜRÜYOR

`A → C → B` zinciri, `C` küçük ve `NoInlining` taşımıyor. Kontrol grubunda `D` aynı şekle sahip
ama `[MethodImpl(MethodImplOptions.NoInlining)]` taşıyor.

| Yapılandırma | sync `A→C→B` | kontrol `A→D[NoInlining]→B` | async `A→C→B` |
|---|---|---|---|
| Varsayılan (tiered on), 400.000 çağrı sonrası | **3/3** | 3/3 | 3/3 |
| `TieredCompilation=0` | **1/3 — yalnız B** | **2/3** (D kaldı, A düştü) | **3/3** |
| `+ TieredPGO=0 + ReadyToRun=0` | **1/3** | 2/3 | **3/3** |

Üç sonuç:

1. **Üç çerçeveden ikisi silindi.** Varsayımsal değil.
2. **Sebep gerçekten inlining.** Kontrol grubu kanıtlıyor: `NoInlining` taşıyan `D` ayakta kaldı,
   aynı şekle sahip `C` kalmadı. Başka bir açıklama bu farkı üretemez.
3. **Async zincirler bağışık.** Üç yapılandırmanın üçünde de 3/3; durum makinesinin `MoveNext`'i
   gerçek bir fiziksel çerçeve.

Varsayılan tiered yapılandırmada 400 bin çağrıdan sonra bile düşmedi. **Mekanizmasını
açıklamıyorum çünkü ölçmedim** — yalnız iki yapılandırmanın farklı davrandığını kaydediyorum.

### %38 — bu bir kenar durum değil, düzenli vaka

Hedefteki 255 metot benzeri düğüm:

| | adet | oran |
|---|---:|---:|
| async / `Task` döndüren — **bağışık** | 158 | %62 |
| **senkron — inline riski altında** | **97** | **%38** |

Ve bunlar tam da inline'a en uygun olanlar: `Cart.RemoveItem`, `CartErrors.ItemNotFound`,
`Money.Add`, `Result.Failure`, `CartResponse.FromCart` — birkaç satırlık domain yardımcıları.

> **Alt-hükmün gerekçesi bu sayıdır.** Ardışık iki çerçevenin arasındaki boşluk "nadiren olur" bir
> şey değil; graph'ın bildiği metotların **%38'i** düşürülebilir durumda. Bu yüzden `graph'ta yok`
> hükmü ikiye ayrıldı.

### Köprü GENİŞLETİLMEDİ

Çözüm arayüz köprüsünü 2 hop'a çıkarmak **değil** — o, Ö4'ün gerekçesini bozar ve gerçekten bir
çağrı olmayan yolu "doğrulandı" saydırır. Bunun yerine:

| Alt-hüküm | Koşul | Rapor metni |
|---|---|---|
| `graph'ta yok` | A'dan B'ye hiç yol yok | graph bu çağrıyı hiç bilmiyor |
| `graph'ta yok — atlanmış çerçeve olabilir` | graph'ta **N ≥ 2 hop'luk** yol var | *"N hop'luk yol var, atlanmış çerçeve olabilir"* + yolun düğümleri |

İkincisi bir **iddia değil, gözlem**: "graph şu yolu biliyor" ölçülebilir bir olgu; "inline edildi"
ise ancak olasılık olarak yazılıyor. → `known-limitations.md` **L20**.

---

## 3. Adım 0c — fixture'lar gerçek mi? **Beşi de gerçek, sentetik yok**

Planın ilk hâli "üç gerçek yığın izi" diyordu ama nasıl üretileceğini yazmıyordu. Elle yazılmış bir
yığın izi 0a'nın ölçtüğü biçimi **taklit eder** — yani parser'ı, test etmesi gereken şeye karşı
değil kendi varsayımına karşı test eder.

Docker ayaktaydı (29.0.1) ve ModularCommerce'in kendi entegrasyon testleri zaten
`new NaiveReservationStrategy(context)` ve `new ProductVectorRepository(fixture.DataSource)`
çağırıyordu. Harness **derlenmiş DLL'leri** referans aldı (`ProjectReference` yok) — hedef repoya
tek bayt yazılmadı.

| # | Basamak | Ariza şekli | İndiği satır | Ö6 tam-satır |
|---|---|---|---|---|
| **A** | 1/2 · gerçek Postgres 17 | bozuk audit trigger → ham SQL UPDATE düşüyor | `NaiveReservationStrategy.cs:`**37** | ✅ **1/1** |
| **A2** | 1/2 · gerçek Postgres 17 | `UpdatedAtUtc` kolonu yok (eksik migration) | `NaiveReservationStrategy.cs:`**16** | ✗ 0/1 |
| **B** | 1/2 · gerçek pgvector | `product_embeddings` tablosu yok | `ProductVectorRepository.cs:`**65** ve **71** | ✗ 0/3 |
| **C** | 3 · altyapısız | `Money.Add` para birimi uyuşmazlığı | `Money.cs:`**35** | — |
| **D** | 1/2 · gerçek Postgres 17 | A ile aynı ariza, **contract adapter üzerinden** | `StockReservationService.cs:`**22** → `:37` | ✅ 1/1 |

**Kabul kriteri (en az 2 gerçek) karşılandı: 5 gerçek, 0 sentetik.**
`ASyntheticFixtureMatchesTheMeasuredFrameShape` testi konusuz kaldı ve bunu söyleyerek geçiyor —
atlanmıyor.

Her fixture dosyasının başında hangi basamaktan geldiği yazılı, ve
`EveryFixtureDeclaresWhereItCameFrom` + `AtLeastTwoFixturesAreRealCaptures` bunu teste bağlıyor:
biri elle yazılmış bir izle değiştirilirse kriter sessizce düşmez.

### A'nın ilk denemesi `:37` yerine `:16`'ya düştü — ve ikisi de tutuldu

EF'in kendi SELECT'i de `UpdatedAtUtc`'yi adlandırıyor, yani akış ham SQL'e hiç varmadan
kırılıyordu. İstenen satırı elde etmek için izi düzenlemek, sentetiği gerçek diye sunmak olurdu.
Bunun yerine mekanizma değiştirildi (trigger) **ve ilk deneme de fixture olarak tutuldu**: A tam
satır isabetini, A2 aynı dosyada satırın tutmadığı negatif vakayı gösteriyor.

### D sonradan eklendi — arayüz köprüsünü başka hiçbir yakalama test etmiyordu

İlk dört fixture'da strateji **doğrudan** çağrılıyordu, yani Ö4'ün arayüz köprüsü hiç
çalışmıyordu. D, arizaya üretimin ulaştığı yoldan gidiyor: `StockReservationService` bir
`IReservationStrategy` tutuyor, DI doğrudan implementasyona dağıtıyor, **arayüz çerçevesi yığın
izinde yok** ama graph'ta iki düğümün arasında duruyor.

---

## 4. Uygulama öncesi ölçümler

### Ö1 — Sembol eşleştirme neredeyse tekil

| | |
|---|---:|
| Method + Handler + Repository düğümü | **255** |
| Farklı `Type.Method` anahtarı (parametresiz) | **254** |
| **Çakışan anahtar** | **1** |

Tek çakışma `ProductChangedConsumer.Consume` — `NodeId`'nin XML yorumunun zaten uyardığı aşırı
yükleme çifti. Ve **çözülemez**: runtime ikisini de `Consume(ConsumeContext\`1 context)` olarak
yazıyor, tip argümanı silinmiş. Rapor bu durumda **hiçbirini seçmiyor**, ikisini de listeliyor.

### Ö2 — Eşleşmeyen çerçeve nadir değil, kural

| | |
|---|---:|
| ModularCommerce `src/` altındaki `.cs` | 300 |
| Graph'ta en az bir düğümü olan | **153** |
| **Hiç düğümü olmayan** | **147** |

Validator'lar, DTO'lar, value object'ler. `Money.Add` bunlardan biri ve `throw` ediyor — fixture C
tam bu vaka. Bu yüzden `graph'ta yok` hükmü kozmetik değil, raporun asıl bilgisi.

### Ö3 — Çağrı yeri eşleştirmesi %90 tekil

507 farklı (çağıran, dosya:satır) çağrı yerinin **457'si (%90)** tek hedefe gidiyor. Kalan %10
ambiguous interface çözümü ya da aynı satırda iç içe çağrı; rapor o durumda hepsini yazıyor.

### Ö4 — Yığın izinde arayüz çerçevesi yok, graph'ta var

| | |
|---|---:|
| Hedefi arayüz düğümü olan `CALLS` kenarı | **89 / 512** |
| Çağrı yeri olmayan kenar | 73 |
| ...arayüz→implementasyon olanı | **72** |

Köprü kuralı bu ölçümden çıkıyor: **ortadaki kenarın çağrı yeri olmamalı.** Bu, düğümün adından
yapılan bir tahmin değil, verinin ölçülebilir bir özelliği.

### Ö9 — git hacmi: üst sınır gerekmiyor

| | |
|---|---:|
| ModularCommerce'te toplam commit | 23 |
| Rapor başına farklı dosya | **2–3** |
| Rapor başına toplam commit satırı | **2–5** |

"10 çerçeve → 6 dosya → 30 satır" endişesi bu hedefte gerçekleşmiyor. **Sabit bir üst sınır
konmadı** — koyulacak sayı elimizde olmayan bir repodan gelirdi, yani Faz 5 §9.6'nın reddettiği
vekil-eşik tahmini olurdu. Yerine rapor **dosya sayısını ve commit satırı sayısını yazıyor**;
sınırsız bir vaka görünür olur ve o zaman ölçülmüş bir eşik konur.

---

## 5. Beş fixture'ın ölçülen çıktısı

| | çerçeve | yabancı (farklı) | eşleşen | graph'ta yok | bağ | kök | tablo | tam-satır | dosya/commit | exit |
|---|---:|---|---:|---:|---|---|---:|---|---|---:|
| **A** | 14 | 12 (8) | 2 | 0 | 1 aynı metot | 2 endpoint | 2 | **1/1** | 3 / 5 | 0 |
| **A2** | 16 | 15 (11) | 1 | 0 | — | 2 endpoint | 2 | 0/1 | 3 / 5 | 0 |
| **B** | 11 | 9 (6) | 2 | 0 | 1 aynı metot | 1 endpoint | **0** | 0/3 | 2 / 2 | 0 |
| **C** | 2 | 1 (1) | 0 | **1** | — | — | — | — | 0 / 0 | **4** |
| **D** | 15 | 12 (8) | 3 | 0 | **1 doğrulandı (arayüz köprüsü)** + 1 aynı metot | 2 endpoint | 2 | 1/1 | 3 / 5 | 0 |

Okunacak üç satır:

- **A**: hata `:37`'de ve raw-SQL diagnostic'i **tam o satırda**. Rapor *"hata noktası graph'ın
  bakamadığı bir bölgede"* diyor ve altındaki tablo listesinin eksik olabileceğini söylüyor.
- **B**: **0 tablo** — ama bu "veriye dokunmuyor" değil. Aynı dosyada 3 raw-SQL diagnostic'i var,
  hiçbiri tam satırda değil, ve rapor ikisini karıştırmıyor.
- **C**: hiç eşleşme yok, çıkış kodu **4**. Rapor *"Bu, 'hiçbir şeye dokunmuyor' demek DEĞİL —
  graph bu çerçeveyi tanımıyor demek"* diyor.

---

## 6. Çıkış kodları — CI için

| Durum | Exit | Raporda eksik olan |
|---|---:|---|
| Her şey tamam | **0** | — |
| `git` PATH'te yok | **3** | son commit'ler, HEAD |
| `--repo` verilen dizin yok / `rev-parse` başarısız | **3** | son commit'ler, HEAD (denenen yol + red sebebi yazılır) |
| `git log` bir dosya için hata verdi | **3** | yalnız o dosyanın commit'leri |
| Hiçbir çerçeve bir düğümle eşleşmedi | **4** | giriş noktaları, tablolar |
| `graph.json` bulunamadı | **4** | her şey (rapor üretilmez) |
| Girdi verilmedi / hatalı seçenek | **64** | — |

**3, mevcut `ExitIncomplete` sabiti.** Yeni bir kod eklenmedi: anlamı zaten *"analiz koştu ama
bilerek eksik"*. Gerekçe `design-decisions.md` D3.

---

## 7. Üç mutasyon koşusu — ve ikincisinin bulduğu şey

| Mutasyon | Sonuç |
|---|---|
| Eşleşmeyen çerçeveleri sessizce at | **5 fixture'ın 5'inde düştü**, kaç çerçevenin kaybolduğunu sayarak: `15→3`, `11→2`, `14→2`, `16→1`, `2→0`. Toplam 9 test |
| Arayüz köprüsünü 2 hop'a çıkar | **HİÇBİR TEST DÜŞMEDİ** — aşağıya bakın |
| `atlanmış çerçeve olabilir` alt-hükmünü kaldır | `AGapWhereTheGraphKnowsARouteIsReportedAsSkippedFrames` düştü: `Expected: SkippedFrames / Actual: MissingEdge` |

### ⚠ Mutasyon 2 testleri kırmadı — ve bu bir bulgu

Köprüyü 2 hop'a çıkarıp "ortadaki kenarın çağrı yeri olmasın" şartını kaldırdım. **50 testin 50'si
geçti.**

Sebep: testler doğruydu, **popülasyon sessizdi.** Beş fixture'ın hiçbirinde tam olarak iki gerçek
hop uzaklıkta bir çerçeve çifti yok. Tek köprülü çift (D) gerçek bir 1 hop'luk arayüz köprüsü ve
genişletilmiş kural altında da doğru şekilde doğrulanıyor.

Graph'ta bu şekilden **310 tane** var (`A --çağrı--> X --çağrı--> B`, `A→B` doğrudan kenarı yok).
Örnek: `AddItemHandler.HandleAsync @42 → Cart.AddItem → Result.Failure`. Ve `Cart.AddItem` senkron
bir domain metodu — yani 0b'nin inline edip düşürdüğü tam o sınıf.

Eklenen `ATwoHopRouteThroughARealCallIsNotVerified` çifti **fixture'dan değil graph'tan** seçiyor ve
mutasyonu isimle bildirerek düşüyor:

```
AddItemHandler.HandleAsync -> Result.Failure: aradan gecen Cart.AddItem GERCEK bir cagri ile
ulasiliyor, DI ile degil. Bu 'dogrulandi' olamaz.
```

Bu bulgunun dersi ve kural hâli **§7a**'da ayrı başlık altında.

---

## 7a. Test doğruydu, popülasyon sessizdi

Bu fazın kaydedilmeye en değer bulgusu bir kusur değil, bir **kör nokta sınıfı**.

### Ne oldu

Arayüz köprüsünü 1 hop'tan 2 hop'a çıkardım ve "ortadaki kenarın çağrı yeri olmasın" şartını
kaldırdım — yani doğrulama kuralını bilerek bozdum. **50 testin 50'si geçti.**

Kuralı koruyan test (`AVerifiedFrameLinkReallyExistsInTheGraph`) doğru yazılmıştı: doğrudan kenarı
ya da tam bir çağrı-yeri-siz hop'u arıyordu, mutasyonu görmesi gerekirdi. Görmedi, çünkü **beş
fixture'ın hiçbirinde tam iki gerçek hop uzaklıkta bir çerçeve çifti yoktu.** Tek köprülü çift (D)
gerçek bir 1 hop'luk arayüz köprüsü ve genişletilmiş kural altında da doğru şekilde doğrulanıyor.

Graph'ta bu şekilden **310 tane** var: `AddItemHandler.HandleAsync @42 → Cart.AddItem →
Result.Failure`. Ve `Cart.AddItem` **senkron** bir domain metodu — yani 0b'nin ölçüp inline edip
düşürdüğü sınıfın ta kendisi (%38'lik popülasyon). Yani kaçırılan vaka egzotik değil, fazın
merkezindeki risk.

Eksik testi **fixture'dan değil graph'tan** vaka seçerek ekledim
(`ATwoHopRouteThroughARealCallIsNotVerified`), ve mutasyon artık isimle bildirerek düşüyor.

### Faz 5'in dersinden farkı — bir seviye yukarısı

| | Faz 5 §11.6 | Faz 6 |
|---|---|---|
| Mutasyon | Testi kırmadı | Testi kırmadı |
| Sebep | **Test yanlış satırı koruyordu** — komşuluk sıralamasını mutasyona uğratmıştım, oysa kardeş sırasını çıkıştaki son sıralama üretiyor | **Test doğru satırı koruyordu** — ama onu tetikleyecek **veri** fixture setinde yoktu |
| Düzeltme | Doğru satırı mutasyona uğrat | Popülasyonu genişlet |

Faz 5'te sorulacak soru *"testim gerçekten neyi koruyor?"* idi. Buradaki soru başka:
***"testimi tetikleyecek girdi elimde var mı?"***

İkisi de aynı yanılgının iki yüzü: **yeşil bir suite, kapsanmayan bir vakayı kapsanmış gösterir.**
Ve ikisi de yalnız mutasyonla görünür oluyor — çünkü mutasyon, testin *var olduğunu* değil
*işlediğini* sorgular.

### Kural — sonraki fazlarda da geçerli

> **Bir mutasyon testi kırmıyorsa, önce testi değil POPÜLASYONU sorgula. Eksik testi fixture'dan
> değil GRAPH'tan seçerek yaz.**

Gerekçe: fixture seti bir **örneklem**, graph ise **popülasyonun kendisi**. Örneklemden test vakası
seçmek, örneklemin zaten içerdiği şekilleri test etmek demektir — tanım gereği hiçbir boşluğu
bulamaz. Graph'tan seçmek ise "bu şekilden kaç tane var?" sorusunu sorulabilir kılar; cevap 310
çıktığında testin eksikliği de, o eksikliğin ağırlığı da ölçülmüş olur.

Pratikte üç adım:

1. Mutasyonu uygula. Test düşmezse **durma, düzeltmeyi geri alma.**
2. `graph.json` üzerinde *"mutasyonun yanlış cevap üreteceği şekilden kaç tane var?"* diye say.
   Sıfırsa mutasyon anlamsızdır; sıfırdan büyükse **test popülasyonu eksiktir.**
3. Test vakasını o sayımdan seç — düğüm id'lerini elle yazma, graph'tan sorgula. Böylece hedef repo
   değiştiğinde vaka da kendiliğinden güncellenir.

Aynı fazda `AGapWhereTheGraphKnowsARouteIsReportedAsSkippedFrames` de bu kuralla yazıldı:
`CheckoutHandler.HandleAsync` ile `NaiveReservationStrategy.ReserveAsync` arasındaki **4 hop**'luk
yol graph'tan bulunuyor, fixture'dan değil.

> **Faz 7 için doğrudan sonucu var.** Eval set, tanımı gereği bir fixture setidir — 20 soru. Roadmap
> zaten *"recall %100 çıkarsa şüphelen, eval set çok kolay demektir"* diyor; buradaki kural bunun
> uygulanabilir hâli: her soru için *"bu sorunun yakaladığı hata sınıfından graph'ta kaç örnek
> var?"* sorulmalı. Sorunun kapsadığı sınıf tek örnekliyse eval set o kategoriyi ölçmüyor,
> yalnız o örneği ölçüyor.

---

## 7b. Faz 5'in markdown kapısı bu fazın kodunda bir kusur buldu

Faz 5'te bulunan lazy-continuation kusuru bir **sınıftı**, tek satır değil. Aynı kural bu fazın
markdown'ına da uygulandı (`NoBlockStartsWithoutABlankLineBeforeIt`, beş fixture üzerinde) ve
**ilk koşuda düştü**:

```
B:74 "**Bilinen sınırlar** bölümü söyler." after
     "**Hiçbiri.** Bunun "veriye dokunmuyor" mu yoksa "bakamadım" mı olduğunu"
```

`**` ile başlayan bir satır, boş olmayan bir satırın hemen ardından geliyordu — parse edilir,
**yanlış render olur**, ve ne derleyici ne 275 testin geri kalanı görür. İki satır tek satıra
birleştirildi.

> Kuralı bir fazda öğrenip bir sonrakinde uygulamamak, kuralı hiç öğrenmemekle aynı sonucu verir.
> Kapı taşındığı için kusur üretilir üretilmez yakalandı.

Aynı koşuda ikinci bir yanlış iddia da düzeltildi: git bölümü koşulsuz *"çıkış kodu 3"* yazıyordu,
oysa hata noktası da bulunamamışsa süreç **4** ile bitiyor. Rapor artık gerçekleşmeyen bir koşuyu
anlatmıyor, ve iki test bunu sabitliyor.

---

## 8. Determinizm — Faz 5'in dersi triage'da

Faz 5: *sıralamayı deterministik yapmak yetmez, keşfin kendisi deterministik olmalı.* Buradaki
keşif **çerçeve eşleştirme**.

| Kural | Neden |
|---|---|
| Aday düğüm birden fazlaysa **hiçbiri seçilmez** | `graph.Nodes` sırası cevabı etkileyemesin. Belirsizlik raporda yazılır |
| Anahtar kovaları id'ye göre sıralı (bir kez, kurucuda) | Aynı gerekçe, tek yerde |
| Repo kökü adayları `SortedDictionary` | Birden fazla çerçeve farklı kök ima ederse sonuç sıraya bağlı kalmasın |
| git dosya listesi tekilleştirilmiş + ordinal sıralı | — |
| **Zaman damgası yok** | Faz 5 kuralı. Git tarafı **HEAD sha'sıyla** sabitleniyor: rapor hangi commit'i anlattığını söyler, ne zaman yazıldığını değil |

`TheReportIsTheSameWhateverOrderTheGraphArrivesIn` node ve kenar listesini **ters çevirip** aynı
baytları istiyor — "iki koşu tuttu"dan güçlü bir iddia, Faz 4'ün testinin biçimi.

---

## 9. Ne yapılmadı

| | Neden |
|---|---|
| Otomatik branch / fix / git write | `design-decisions.md` D1 — ve kural değil, **yüzey**: `GitLog` yalnız `rev-parse` ve `log` çıkarabiliyor |
| LibGit2Sharp | D2 — yeni NuGet, roadmap kuralı 3 |
| HTTP `/triage` ucu | Kayıtlar Core'da ve taşımadan bağımsız; ileride ince bir kabuk olur. Bu fazda istenmedi |
| Diyagram adım numarasının yeniden kullanımı | Ölçüldü ve reddedildi: hata çerçevesi genellikle diyagramda **yok**. `post-api-inventory-reservations.md` 1 adım gösteriyor, hata 3 kat derinde. "Bu akışın 1. adımı" demek uydurma olurdu |
| Yeni node/kenar tipi | Ontoloji büyümedi |
| LLM | Faz 8'e kadar yok — özet cümlesi bile |

---

## 10. Sayılar

| | |
|---|---|
| Test | **275**, 0 atlanan (Faz 5 sonu 206 → +69) |
| Fixture | 5, **hepsi gerçek**, 0 sentetik |
| Determinizm (Faz 5) | 37/37 byte-identical |
| Mermaid parse | 26/26 |
| Markdown blok kapısı | 0 ihlal |
| GitHub sütununu aşan | 6 / 25 — `out/` bayt bayt **değişmedi** |
| `graph.json` | **değişmedi** — bu faz graph üretmiyor |
| Sembol anahtarı | 255 düğüm → 254 anahtar, 1 çözülemez çakışma |
| Graph'ta düğümü olmayan kaynak dosyası | 147 / 300 |
| Senkron düğüm (inline riski) | **97 / 255 (%38)** |
| Çağrı yeri tekilliği | 457 / 507 (%90) |
| Arayüz köprüsü gereken kenar | 89 / 512 |
| Rapor başına git hacmi | 2–3 dosya, 2–5 commit satırı |
