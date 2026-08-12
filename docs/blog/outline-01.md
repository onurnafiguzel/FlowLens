# Medium yazısı ana hattı (rev. 2, onaylandı)

Yazı: `docs/blog/article-01.md`

## Tez (seçildi)

> **Bir araç ne bulduğuyla değil, bulamadığını söyleyebilmesiyle güvenilir olur.**

Bu projede bulunan her ciddi hata bir yokluktu. Ayırt edici özellik: *"dokunmuyor"* ile
*"bakamadım"* birbirinden ayrılıyor. Ham SQL noktaları `file:line` ile raporlanıyor; recall tek
bir ortalama yerine eksen eksen yayınlanıyor (EF içi %97,1, ham SQL %0); eval set kendi hata
payını ölçüp üç düzeltme yayınlıyor.

Üç problem bu tezin taşıyıcısı: onboarding'de yanlış harita günlerce yanlış yön, change impact
analysis'te eksik kolon production hatası, incident triage'da yanlış giriş noktası yanlış ekip
demek.

Sonuçları: §7 (Sınırlar) yazının zayıf noktası değil, tezin kanıtı; kısaltılmayacak. Kibana
testinin sonucu tezin kendi örneği. `out/`'un kendi cümleleri zaten bu tezi söylüyor.

## P3 varsayımının testi: ölçüldü, kısmen çürütüldü

Varsayım: *"Kibana'dan kopyalanan ham bir exception metnini doğrudan `flowlens triage`'a
versek çalışır."*

Sekiz varyant gerçek CLI'ya stdin üzerinden verildi (`--stack-trace -`), hiçbir dosya yazılmadan.
Hepsi `A-inventory-reserve.txt` fixture'ından türetildi.

| # | Girdi biçimi | exit | Çerçeve | Sonuç |
|---|---|---:|---:|---|
| T1 | Ham metin, gerçek satır sonları, `#` yorumları silinmiş | 0 | 14 | çalışıyor |
| T2 | Tek satır, literal `\n` kaçışlı | 4 | 0 | çalışmıyor |
| T3 | JSON'a gömülü (`_source.message`) | 4 | 0 | çalışmıyor |
| T4 | Log öneki başlığa yapışık | 0 | 14 | kısmi: tip boş, gerisi tam |
| T5 | T2 + ön işleme (`\n` gerçek satır sonuna) | 0 | 14 | çalışıyor |
| T6 | T3 + ön işleme (`message` alanı çıkarıldı) | 0 | 14 | çalışıyor |
| T7 | Kırpılmış iz, yalnız framework çerçeveleri | 4 | 6 | çalışmıyor |
| T8 | Bozuk `at` satırı (parantez yok) | 4 | 3 | çalışmıyor, `unparsed=1` raporlandı |

Dört bulgu:

1. Yorum satırları hiç sorun değildi. Parser her satırı sınıflandırıyor; `#` ile başlayanlar
   `Text` kovasına düşüp yok sayılıyor. Varsayımın bu yarısı doğru.
2. Kırılma noktası satır sonu. `Parse` girdiyi `\n` ile bölüyor (`StackTraceParser.cs:92`), her
   çerçeve `^at\s+...` regex'ine uymak zorunda (`:451`). Kibana'nın tek satırlık ve JSON
   biçiminde gerçek satır sonu yok, sonuç sıfır çerçeve ve `exit 4`.
3. Ön işleme tek satırlık (T5, T6). Komutlar §6.5'te.
4. T4 tezin doğrudan örneği, §7'de açılıyor.

## Bölüm planı

Hedef uzunluk ~2.900 kelime (2.500-3.000 bandı).

| § | Bölüm | Kelime | Kitle |
|---|---|---:|---|
| 0 | Açılış sahnesi + tez cümlesi | 230 | herkes |
| 1 | Üç problem, adlarıyla | 240 | analist |
| 2 | Çözümün şekli: neden bir dosya | 250 | herkes |
| 3 | Üç proje ve bağımlılık yönü | 200 | developer |
| 4 | Yedi faz, yedi çıktı | 330 | herkes |
| 5 | `out/`, yazının merkezi | 600 | herkes |
| 6 | Üç senaryo, adım adım | 300 | analist + lider |
| 6.5 | Gerçek dünya girdisi: Kibana | 130 | developer + lider |
| 7 | Sınırlar, dürüstçe | 330 | herkes |
| 8 | Kendi projende | 250 | developer |
| 9 | Kapanış | 80 | herkes |

Kitle ayrımı: ana omurga karışık kitleye yazılır. Developer'a özel her şey
`> **Developer notu**` bloklarına alınır ve atlanabilir olduğu §0'da bir kez söylenir. Roslyn en
fazla bir cümle: *"C# derleyicisinin kendisi bir kütüphane."* API adı yok.

### §0, ~230 kelime, herkes
Üç kişi, tek sabah: analist masaya gidiyor, yeni giren üçüncü gününde soruyor, 14:40'ta
production'da 500'ler başlıyor. Ortak zemin: kimse yalan söylemiyor, kimsede harita yok.
Bölümün son paragrafı tez. Görsel yok.
**Okuyucu ne öğrenecek:** üç problemin aynı eksiklikten doğduğu, ve yazının nereye gideceği.

### §1, ~240 kelime, analist
P1 onboarding, P2 change impact analysis, P3 incident triage. Her biri için bugün nasıl
çözüldüğü ve maliyeti. Tez cümlesi burada yok, §0'da verildi. Görsel: küçük tablo.
**Okuyucu ne öğrenecek:** bunların adı olan, endüstride karşılığı olan problemler olduğu.

### §2, ~250 kelime, herkes
Kaynak kod, tek bir `graph.json` (415 node, 966 kenar, 652 KB). 66 projeyi derlemek 25-32
saniye, bu bir HTTP cevabı olamaz. Pahalı hesap günde bir kez, ucuz sorgu binlerce kez:
0,4-1,9 ms. Görsel: mimari şema.
**Okuyucu ne öğrenecek:** ayrımın performans değil tasarım kararı olduğu.

### §3, ~200 kelime, Developer notu
`Core`, `Cli`, `Api`. `Core` hiçbirine referans vermiyor. LLM projesi dördüncü kutu olacak ve
`Core` ona da referans vermeyecek: kapalıyken her şey çalışmaya devam ediyor.
**Okuyucu ne öğrenecek:** tek yönlü bağımlılığın temizlik değil çıkarılabilirlik ürettiği.

### §4, ~330 kelime, herkes
Faz adları değil ürettikleri, ve üçüncü sütun: olmasaydı ne olurdu.

| Ne üretildi | Hangi problemi çözdü | Olmasaydı |
|---|---|---|
| Solution 66/66 güvenilir yükleniyor | zemin | Sessizce atlanan bir proje, "bu metot çağrılmıyor" ile "hiç bakmadık"ı ayırt edilemez yapardı |
| 25 endpoint + çağrı zinciri | P1'in yarısı | Endpoint'lerin tamamı Minimal API lambda'sı; klasik metot taraması 25'in sıfırını görüyor |
| `graph.json`, 16 tablo, 97 kolon | P2'nin çekirdeği | Her soru için 25-32 saniyelik derleme; hiçbir tüketici mümkün olmazdı |
| 5 uçlu HTTP API, ~2 ms | P2 çözüldü | Analistin cevap alması için `dotnet run` çalıştırması gerekirdi |
| `out/`, 37 dosya | P1 çözüldü | Yeni gelen hâlâ araç kurup komut yazmak zorunda kalırdı |
| `flowlens triage` | P3 çözüldü | Backward zaten vardı; eksik olan sormanın yoluydu |
| 22 soruluk eval set | üçünün ölçülmüş doğruluğu | 110 test yeşilken graph üç yerde sessizce yanlış cevap veriyordu; hiçbir sayı ölçülmemiş olurdu |

**Okuyucu ne öğrenecek:** her fazın bir durak noktası ürettiği.

### §5, ~600 kelime, herkes. Katalog değil, kullanım.

**5a (~110):** `out/README.md`. Kapsam uyarısı alıntısı ve "Veri katmanına dokunmayan (2)"
bölümü: negatif sonucun sonuç olarak yayınlanması.

**5b (~300):** Tek soru, tek sayfa. *"Sepet silme akışı neye dokunuyor?"* Vehikül
`out/flows/delete-api-cart-items-productid-guid.md`, çünkü tek ekrana sığıyor (10 kutu) ve dört
parçanın dördünü birden taşıyor. Anlatım sırası sorunun cevaplanma sırası:
diyagram (kaba cevap) → veri katmanı (kesin cevap: `WR`, üç kolon) → numaralı adımlar (2. ve 3.
adım `koşullu` ve birbirini dışlıyor) → bilinen sınırlar (liste tam mı). Kapanışta sayfanın
kendini ölçen satırı, ve ölçekte ne olduğunu gösteren tek cümlelik geri dönüş (checkout 24/192).

**5c (~80):** İki karşıt sayfa. `get-api-catalog-products.md`: `R`, kolon `—`.
`post-api-discovery-search.md`: `## Veri katmanı` bölümü hiç yok, yerine üç `file:line`.

**5d (~110):** `dependencies.md`. 8 modül, 9 kenar, üç kenar stili. İki ihlal adayı da ters
yönde. İki alıntı: "Bu bir hüküm değil, bir işaret" ve 204 kenar paragrafı.

**5e (~40):** `Ordering.md`. Beş bölüm. Layout'un tek başına ürettiği bulgu:
`OrderCancelled` yayınlanıyor, tüketici yok.

**Okuyucu ne öğrenecek:** bu dosyaların deterministik üretildiği ve GitHub'da hiçbir şey
kurmadan açıldığı.

### §6, ~300 kelime, analist + lider
Her biri: kim, ne yapıyor, ne görüyor, ne kadar sürüyor. Kibana ayrıntısı burada yok.
P1 onboarding (~100), P2 change impact analysis (~110), P3 incident triage (~90).
P2'nin sonunda yazılımcının rolünün değiştiği, ortadan kalkmadığı.

### §6.5, ~130 kelime, developer + lider
Kibana testinin üç satırlık özeti ve iki komut. Üçüncü satırın niye ilginç olduğu §7'ye bırakılır.
**Okuyucu ne öğrenecek:** aracın ne kadar ön işleme istediği ve başarısızlığın sessiz olmadığı.

### §7, ~330 kelime, herkes. ATLANMAYACAK.
Eksen eksen recall tablosu, tek ortalama yok. Dört körlük sebepleriyle. §6.5'in üçüncü satırı
burada açılıyor: araç zaman damgasını exception tipi diye raporlamaktansa alanı boş bırakıyor.
Kapanış: en tehlikeli çıktı boş kümedir.

### §8, ~250 kelime, Developer notu
Ön koşullar: EF Core, Minimal API ya da Controller, tek solution, derlenmiş hedef repo.
Nereden başlanır: graph üretmek değil saymak. Sonra sıra. Ve bir uyarı: ölçmeden "çalışıyor"
deme.

### §9, ~80 kelime
Teze bağlanan üç cümle ve GitHub bağlantıları.

## Alınacak ekran görüntüleri

Ö = öncelikli, İ = ikincil. 21-23 rev. 2'de eklendi (§5b cart-delete'e geçti), 4 ve 5 Ö'den
İ'ye indi. Silinen madde yok.

| # | Ö/İ | Dosya | Hangi bölüm |
|---|---|---|---|
| 1 | Ö | `out/README.md` | Modüller tablosu + Akışlar tablosunun ilk 8 satırı |
| 2 | Ö | `out/README.md` | Kapsam uyarısı blockquote + "Veri katmanına dokunmayan (2)" |
| 3 | Ö | `out/flows/post-api-ordering-checkout.md` | Render olmuş Mermaid |
| 4 | İ | aynı dosya | `## Çağrı sırası`, `HandleAsync` bloğu |
| 5 | İ | aynı dosya | `## Veri katmanı`, 12 satır |
| 6 | Ö | aynı dosya | `## Diyagram neyi göstermiyor` (24 / 192) |
| 7 | İ | aynı dosya | `## Bilinen sınırlar`, beş kod |
| 8 | Ö | `out/flows/post-api-discovery-search.md` | Sayfanın tamamı |
| 9 | Ö | `out/flows/get-api-catalog-products.md` | `## Veri katmanı` (tek satır: `R`, `—`) |
| 10 | İ | `out/flows/post-api-ordering-orders-id-guid-cancel.md` | Render Mermaid + `## Veri katmanı` |
| 11 | Ö | `out/modules/dependencies.md` | Render olmuş Mermaid |
| 12 | Ö | aynı dosya | Üç kenar stilinin legend tablosu |
| 13 | Ö | aynı dosya | `## İhlal adayları` |
| 14 | İ | aynı dosya | `## Shared` paragrafı (204 kenar) |
| 15 | İ | `out/modules/Ordering.md` | `## Event'ler`, `OrderCancelled` satırı |
| 16 | İ | `out/modules/Ordering.md` | `## Bağımlılıklar` |
| 17 | Ö | terminal | `flowlens triage` çıktısı: giriş noktaları, tablolar, son değişiklikler |
| 18 | Ö | terminal | `### ⚠ Hata noktası, graph'ın bakamadığı bir bölgede` bloğu |
| 19 | İ | terminal / Postman | `GET /backward?node=column:cart.carts.Items` |
| 20 | İ | `evals/report.md` | Eksen eksen recall tablosu |
| 21 | Ö | `out/flows/delete-api-cart-items-productid-guid.md` | Render olmuş Mermaid (10 kutu) |
| 22 | Ö | aynı dosya | `## Veri katmanı` + `## Çağrı sırası`, iki `koşullu` adım |
| 23 | Ö | aynı dosya | `## Bilinen sınırlar`, üç kod |

3, 10, 11 ve 21 GitHub'da render olmuş hâlleriyle alınmalı. Ham markdown ekran görüntüsü
"hiçbir şey kurmadan açılıyor" iddiasını zayıflatır.

## Yazım kuralları

1. Roslyn en fazla bir cümle, API adı yok.
2. Her sayı ölçülmüş, nereden geldiği belli.
3. Developer notları görsel olarak ayrı, atlanabilir olduğu bir kez söylenir.
4. Somut örnek, soyut tarif yok. Katalog değil kullanım.
5. Sınırlar bölümü kısaltılmaz.
6. Kibana testi ölçüm olarak anlatılır.
7. Türkçe. Teknik terimler İngilizce: change impact analysis, incident triage, recall,
   precision, endpoint, event.
