# Unity çalışma kuralları

Bu projede çalışan tüm ajanlar aşağıdaki kurallara kesinlikle uymalıdır:

- Unity Editor'ı açmayın, yeniden açmayın, kapatıp başlatmayın ve yeni bir Unity süreci çalıştırmayın. Yalnızca kullanıcının hâlihazırda açık tuttuğu Unity oturumunu kullanın.
- iPhone, fiziksel cihaz veya simülatörde uygulamayı çalıştırmayın; `Build and Run` kullanmayın.
- Tam build, clean build, rebuild, iOS/Xcode build veya Unity batch-mode build başlatmayın.
- Geçici proje kopyasında bile Unity'yi `-batchmode` ile başlatmayın; `FlowCheck.Run` veya benzeri çalışma-zamanı doğrulaması çalıştırmayın.
- Değişikliklerin görünmesi ya da kontrol edilmesi gerektiğinde yalnızca mevcut Unity oturumunda sahneyi yenileyin/yeniden yükleyin. Bunun dışındaki derleme veya çalıştırma doğrulamalarını atlayıp kullanıcıya bildirin.
- `Temp/sorting-shelf-showcase.req` oluşturmayın/yazmayın; `SortingShelfShowcaseBuilder.Build`, `Rebuild Sorting Shelf Showcase`, bake ya da sahne/prefab yeniden üretimi tetiklemeyin.
- Unity lisans ya da yazılım koşulları penceresinde kullanıcı adına seçim yapmayın ve bu pencereyi tetikleyecek yeni Unity süreci başlatmayın.
- Uzun süren tüm-proje taramalarını veya arka plan döngülerini çalıştırmayın. Başlatılan sınırlı bir teşhis süreci tamamlandığında kapandığını doğrulayın; öldürülen/iptal edilen bir işlemi otomatik olarak yeniden başlatmayın.
- Bu kurallardan sapmak gerekirse önce kullanıcıdan açık izin alın. Kendi başınıza istisna yapmayın.
