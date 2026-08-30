# Glass Pour Math Demo (Unity 6)

Bu proje, farklı bardak geometrilerinde hacmi koruyan sıvı katmanlarını ve bardaktan
bardağa dökme animasyonunu içerir.

## Ana bardak referans sahnesi

- `Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab.unity`

Royal Glass Lab, bardakların son görünümü, geometrisi, sıvı profilleri ve dökme
animasyonu için tek canonical referanstır. Royal builder yalnız Royal profil ve
materyallerini kullanır; eski genel profillerden sessizce kopya üretmez.

Sahneyi yeniden üretmek için:

- `Tools > LiquidSort > Rebuild Royal Glass Lab`

Eski `AllGlassesPlayground` sahnesi ve yalnız ona ait görsel zincir proje dışındaki
yerel arşive taşınmıştır. Gerekirse ana projeye değil, ayrı bir deneme projesine
aktarılmalıdır. `Assets/LiquidSort/Profiles` altındaki genel profiller ve bunların
kaynak görselleri gelecekte yeni bardak üretimi için korunur; ikinci bir sahne değildir.

## Korunan çekirdek

- `VesselProfile`: bardak iç poligonu, görünür taban, kapasite ve dökme pozları
- `LiquidBottle`: sıvı bantlarının çizimi ve profil verisi
- `BottleShell`: bardak çerçevesi, gölge ve görsel tema
- `PourAnimator` + `PourStream`: taşıma, eğilme, akış ve geri dönüş animasyonu
- `WaterSortBoard`: seçim ve geçerli sıvı aktarımı
- `VesselProfileBaker`: yeni veya güncellenen bardak profilini üretme

Aktif profillerin `front`, `traceSource`, materyal veya bake tablolarını eski deneme
asset'leriyle değiştirmeyin. Yeni bir bardak eklerken ayrı bir `VesselProfile` oluşturup
profili baker ile yeniden üretin.

## Unity sürümü

Proje `6000.0.30f1` ile oluşturulmuştur. `Library`, `Temp`, `Logs` ve `UserSettings`
yeniden üretilebilir yerel klasörlerdir ve Git'e dahil edilmez.
