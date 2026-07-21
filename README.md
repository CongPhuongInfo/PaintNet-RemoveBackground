# RemoveBackgroundEffect

Plugin **Paint.NET** (classic effect, `PropertyBasedEffect`) tách chủ thể ra khỏi nền bằng thuật toán **GrabCut** của OpenCV — khác với `RemoveTextEffect`/`RemoveSelectedObjectEffect` (xoá 1 vùng bằng inpaint), effect này phân loại từng pixel là "vật thể" hay "nền", rồi:

- xoá nền thành **trong suốt** (alpha = 0), hoặc
- thay nền bằng **màu trắng**.

## Cách dùng

1. Trước khi bật effect, dùng công cụ **Rectangle Select** (hoặc bất kỳ selection nào) của Paint.NET để khoanh một khung **bao quanh chủ thể** (không cần khoanh sát viền — GrabCut sẽ tự tinh chỉnh biên bên trong khung đó).
2. Nếu không có selection, effect dùng khung mặc định là toàn ảnh trừ viền an toàn nhỏ.
3. Vào **Effects → Image Cleanup → Remove Background (GrabCut)** và chỉnh các tuỳ chọn bên dưới.

## Kiến trúc / lưu ý quan trọng

`OnRender` được Paint.NET gọi rất nhiều lần (mỗi tile, nhiều luồng, kể cả khi kéo thanh trượt xem trước). GrabCut khá tốn CPU (chạy nhiều iteration trên toàn ảnh), nên pipeline chỉ chạy **một lần** trong `OnSetRenderInfo` khi cấu hình hoặc selection thật sự thay đổi, kết quả được cache vào một `Surface`. `OnRender` chỉ copy pixel từ cache ra — nhanh và an toàn khi gọi đồng thời.

## Tuỳ chọn cấu hình

| Thuộc tính | Loại | Mô tả |
|---|---|---|
| `OutputMode` | Radio button | `Trong suốt (PNG)` hoặc `Nền trắng` |
| `Iterations` | Slider (1–12) | Số vòng lặp GrabCut — cao hơn = chính xác hơn nhưng chậm hơn |
| `Feather` | Slider (0–20) | Làm mềm viền (px) giữa chủ thể và nền |

## Yêu cầu hệ thống

- **Windows** + **Paint.NET** đã cài sẵn (cần các DLL `PaintDotNet.Effects.Core`, `PaintDotNet.PropertySystem`, v.v.)
- **.NET 9 SDK** — [tải tại đây](https://dotnet.microsoft.com/download)

### Gói NuGet sử dụng

| Package | Vai trò |
|---|---|
| `OpenCvSharp4` | Core OpenCV bindings (GrabCut, Mat, v.v.) |
| `OpenCvSharp4.runtime.win` | Native binaries OpenCV cho Windows |
| `OpenCvSharp4.Extensions` | `BitmapConverter` — chuyển đổi `Bitmap ↔ Mat` |

## Build

Chạy trực tiếp:

```bat
build_RemoveBackgroundEffect.bat
```

Script sẽ:
1. Kiểm tra `dotnet` đã cài chưa.
2. Tự dò thư mục cài Paint.NET (`Program Files\paint.net` hoặc `Program Files (x86)\paint.net`).
3. Build Release ra `bin\RemoveBackgroundEffect.dll`.
4. Hỏi có muốn copy DLL vào thư mục `Effects` của Paint.NET không.

Hoặc build thủ công:

```bat
dotnet build RemoveBackgroundEffect.vbproj -c Release -p:PdnDir="C:\Program Files\paint.net" -o bin
```

## Cài đặt

1. Build xong, copy `bin\RemoveBackgroundEffect.dll` vào `<thư mục Paint.NET>\Effects\`.
2. Khởi động lại Paint.NET.
3. Vào **Effects → Image Cleanup → Remove Background (GrabCut)**.

## Cấu trúc project

```
RemoveBackgroundEffect.vb        Mã nguồn plugin (VB.NET)
RemoveBackgroundEffect.vbproj    Định nghĩa project + reference tới Paint.NET SDK
build_RemoveBackgroundEffect.bat Script build + deploy tự động
```

## Ghi chú

- Nếu build báo lỗi không tìm thấy `PaintDotNet.Effects.Core.dll`, truyền đường dẫn Paint.NET thủ công bằng `-p:PdnDir="..."`.
- Nếu kết quả tách nền còn dính viền/lem chủ thể: tăng `Iterations` để GrabCut chạy kỹ hơn, hoặc khoanh selection sát chủ thể hơn.
- Nếu viền bị cứng/răng cưa: tăng `Feather` để làm mềm biên alpha.
- Chọn `Trong suốt (PNG)` khi cần xuất file có kênh alpha (PNG); chọn `Nền trắng` khi cần ảnh nền phẳng dùng ngay (ví dụ ảnh sản phẩm).
