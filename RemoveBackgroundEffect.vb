Option Strict On

Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports PaintDotNet
Imports PaintDotNet.Effects
Imports PaintDotNet.PropertySystem
Imports PaintDotNet.IndirectUI
Imports OpenCvSharp
Imports OpenCvSharp.Extensions

''' <summary>
''' Remove Background (GrabCut) - Paint.NET classic effect plugin.
'''
''' Khác với RemoveSelectedObjectEffect (xóa 1 vùng bằng inpaint), effect này
''' TÁCH CHỦ THỂ RA KHỎI NỀN: dùng thuật toán GrabCut của OpenCV để phân loại
''' từng pixel là "vật thể" hay "nền", rồi hoặc xóa nền thành trong suốt
''' (alpha = 0) hoặc thay nền bằng màu trắng.
'''
''' CÁCH DÙNG: Trước khi bật effect, dùng công cụ Rectangle Select (hoặc bất kỳ
''' selection nào) của Paint.NET để khoanh một khung BAO QUANH chủ thể (không
''' cần khoanh sát viền - GrabCut sẽ tự tinh chỉnh biên bên trong khung đó).
''' Nếu không có selection, effect dùng khung mặc định là toàn ảnh trừ viền an
''' toàn nhỏ.
'''
''' ARCHITECTURE NOTE (giữ nguyên nguyên tắc từ các effect khác trong bộ):
''' GrabCut khá tốn CPU (chạy nhiều iteration trên toàn ảnh), nên pipeline chỉ
''' chạy 1 lần trong OnSetRenderInfo khi config/selection thật sự đổi, kết quả
''' được cache vào 1 Surface. OnRender (được gọi nhiều lần/luồng cho từng tile,
''' kể cả lúc kéo slider preview) chỉ copy pixel từ cache ra - không chạy lại
''' GrabCut.
'''
''' Requires NuGet packages: OpenCvSharp4, OpenCvSharp4.runtime.win.
''' </summary>
Public NotInheritable Class RemoveBackgroundEffect
    Inherits PropertyBasedEffect

    Private Const PropertyName_OutputMode As String = "OutputMode"
    Private Const PropertyName_Iterations As String = "Iterations"
    Private Const PropertyName_Feather As String = "Feather"

    ' "Trong suốt (PNG)" và "Nền trắng" - index khớp với OnCreatePropertyCollection.
    Private Const OutputMode_Transparent As Integer = 0
    Private Const OutputMode_White As Integer = 1

    ' Cached result của pipeline GrabCut, key theo settings + vùng selection đã dùng để build.
    Private cachedResult As Surface
    Private lastOutputMode As Integer = -1
    Private lastIterations As Integer = -1
    Private lastFeather As Integer = -1
    Private lastSelectionBounds As Rectangle = Rectangle.Empty

    Public Sub New()
        MyBase.New("Remove Background (GrabCut)", CType(Nothing, Image), "Image Cleanup", New EffectOptions() With {
            .Flags = EffectFlags.Configurable
        })
    End Sub

    Protected Overrides Function OnCreatePropertyCollection() As PropertyCollection
        Dim props As New List(Of [Property]) From {
            New StaticListChoiceProperty(PropertyName_OutputMode, New Object() {"Trong suot (PNG)", "Nen trang"}, 0),
            New Int32Property(PropertyName_Iterations, 5, 1, 12),
            New Int32Property(PropertyName_Feather, 2, 0, 20)
        }
        Return New PropertyCollection(props)
    End Function

    Protected Overrides Function OnCreateConfigUI(props As PropertyCollection) As ControlInfo
        Dim configUI As ControlInfo = CreateDefaultConfigUI(props)
        configUI.SetPropertyControlType(PropertyName_OutputMode, PropertyControlType.RadioButton)
        configUI.SetPropertyControlType(PropertyName_Iterations, PropertyControlType.Slider)
        configUI.SetPropertyControlType(PropertyName_Feather, PropertyControlType.Slider)
        configUI.SetPropertyControlValue(PropertyName_OutputMode, ControlInfoPropertyNames.Description, "Ket qua")
        configUI.SetPropertyControlValue(PropertyName_Iterations, ControlInfoPropertyNames.Description, "So vong lap GrabCut (cao hon = chinh xac hon nhung cham hon)")
        configUI.SetPropertyControlValue(PropertyName_Feather, ControlInfoPropertyNames.Description, "Lam mem vien (px)")
        Return configUI
    End Function

    Protected Overrides Sub OnSetRenderInfo(newToken As PropertyBasedEffectConfigToken, dstArgs As RenderArgs, srcArgs As RenderArgs)
        Dim outputMode As Integer = CInt(newToken.GetProperty(Of StaticListChoiceProperty)(PropertyName_OutputMode).Value)
        Dim iterations As Integer = newToken.GetProperty(Of Int32Property)(PropertyName_Iterations).Value
        Dim feather As Integer = newToken.GetProperty(Of Int32Property)(PropertyName_Feather).Value

        ' Vùng Selection hiện tại của Paint.NET dùng làm khung khởi tạo cho GrabCut.
        Dim selectionRegion As PdnRegion = EnvironmentParameters.GetSelectionAsPdnRegion()
        Dim selectionBounds As Rectangle = selectionRegion.GetBoundsInt()

        Dim settingsChanged As Boolean =
            cachedResult Is Nothing OrElse
            outputMode <> lastOutputMode OrElse
            iterations <> lastIterations OrElse
            feather <> lastFeather OrElse
            selectionBounds <> lastSelectionBounds

        If settingsChanged Then
            cachedResult?.Dispose()
            cachedResult = RunPipeline(srcArgs.Surface, selectionBounds, outputMode, iterations, feather)

            lastOutputMode = outputMode
            lastIterations = iterations
            lastFeather = feather
            lastSelectionBounds = selectionBounds
        End If

        MyBase.OnSetRenderInfo(newToken, dstArgs, srcArgs)
    End Sub

    Protected Overrides Sub OnRender(rois As Rectangle(), startIndex As Integer, length As Integer)
        If length = 0 OrElse cachedResult Is Nothing Then Return

        Dim dst As Surface = DstArgs.Surface

        For i As Integer = startIndex To startIndex + length - 1
            Dim rect As Rectangle = rois(i)
            For y As Integer = rect.Top To rect.Bottom - 1
                For x As Integer = rect.Left To rect.Right - 1
                    dst(x, y) = cachedResult(x, y)
                Next
            Next
        Next
    End Sub

    ''' <summary>Chạy GrabCut một lần và trả về Surface full-size (BGRA nếu Transparent, BGR-on-white nếu White).</summary>
    Private Shared Function RunPipeline(srcSurface As Surface, selectionBounds As Rectangle, outputMode As Integer, iterations As Integer, feather As Integer) As Surface
        Using srcBitmap As Bitmap = srcSurface.CreateAliasedBitmap()
            Using srcMatRaw As Mat = BitmapConverter.ToMat(srcBitmap)
                ' GrabCut chỉ chạy được trên ảnh 3 kênh (BGR) - bỏ kênh alpha nếu có.
                Using srcBgr As Mat = ToBgr3Channel(srcMatRaw)

                    Dim imgRect As New Rectangle(0, 0, srcBgr.Width, srcBgr.Height)
                    Dim grabRect As OpenCvSharp.Rect = BuildGrabCutRect(selectionBounds, imgRect)

                    Using mask As New Mat(srcBgr.Size(), MatType.CV_8UC1, Scalar.Black)
                        Using bgdModel As New Mat()
                            Using fgdModel As New Mat()
                                Cv2.GrabCut(srcBgr, mask, grabRect, bgdModel, fgdModel, iterations, GrabCutModes.InitWithRect)
                            End Using
                        End Using

                        ' GC_FGD (1) va GC_PR_FGD (3) = vat the; GC_BGD (0) va GC_PR_BGD (2) = nen.
                        Using alphaMask As New Mat(srcBgr.Size(), MatType.CV_8UC1, Scalar.Black)
                            Cv2.InRange(mask, New Scalar(1), New Scalar(1), alphaMask)
                            Using prFgMask As New Mat()
                                Cv2.InRange(mask, New Scalar(3), New Scalar(3), prFgMask)
                                Cv2.BitwiseOr(alphaMask, prFgMask, alphaMask)
                            End Using

                            If feather > 0 Then
                                Dim k As Integer = feather * 2 + 1
                                Cv2.GaussianBlur(alphaMask, alphaMask, New OpenCvSharp.Size(k, k), 0)
                            End If

                            Dim resultBitmap As Bitmap
                            If outputMode = OutputMode_White Then
                                Using resultBgr As Mat = ComposeOnWhite(srcBgr, alphaMask)
                                    resultBitmap = BitmapConverter.ToBitmap(resultBgr)
                                End Using
                            Else
                                Using resultBgra As Mat = ComposeTransparent(srcBgr, alphaMask)
                                    resultBitmap = BitmapConverter.ToBitmap(resultBgra)
                                End Using
                            End If

                            Using resultBitmap
                                Return Surface.CopyFromBitmap(resultBitmap)
                            End Using
                        End Using
                    End Using
                End Using
            End Using
        End Using
    End Function

    ''' <summary>Chuyển Mat bất kỳ (BGR/BGRA/xám) về đúng BGR 3 kênh cho GrabCut.</summary>
    Private Shared Function ToBgr3Channel(src As Mat) As Mat
        Dim dst As New Mat()
        Select Case src.Channels()
            Case 4
                Cv2.CvtColor(src, dst, ColorConversionCodes.BGRA2BGR)
            Case 1
                Cv2.CvtColor(src, dst, ColorConversionCodes.GRAY2BGR)
            Case Else
                src.CopyTo(dst)
        End Select
        Return dst
    End Function

    ''' <summary>
    ''' Khung chữ nhật khởi tạo cho GrabCut, lấy từ bounding box của Selection.
    ''' Nếu Selection = toàn ảnh (mặc định khi người dùng chưa khoanh gì), thu
    ''' nhỏ khung vào trong một chút để GrabCut còn "vùng chắc chắn là nền" ở
    ''' ngoài khung - nếu không GrabCut sẽ không có gì để phân biệt.
    ''' </summary>
    Private Shared Function BuildGrabCutRect(selectionBounds As Rectangle, imgRect As Rectangle) As OpenCvSharp.Rect
        Dim isWholeImageSelected As Boolean = selectionBounds = imgRect OrElse selectionBounds = Rectangle.Empty

        Dim r As Rectangle
        If isWholeImageSelected Then
            Dim marginX As Integer = Math.Max(1, imgRect.Width \ 20)
            Dim marginY As Integer = Math.Max(1, imgRect.Height \ 20)
            r = Rectangle.Inflate(imgRect, -marginX, -marginY)
        Else
            r = Rectangle.Intersect(selectionBounds, imgRect)
        End If

        ' GrabCut yeu cau rect co dien tich > 0 va nam trong anh.
        If r.Width < 2 Then r.Width = 2
        If r.Height < 2 Then r.Height = 2
        r = Rectangle.Intersect(r, imgRect)

        Return New OpenCvSharp.Rect(r.X, r.Y, r.Width, r.Height)
    End Function

    ''' <summary>Ghép BGR + mask alpha (0-255) thành Mat BGRA 4 kênh (nen trong suot).</summary>
    Private Shared Function ComposeTransparent(srcBgr As Mat, alphaMask As Mat) As Mat
        Dim channels As Mat() = Cv2.Split(srcBgr)
        Try
            Dim merged As New Mat()
            Cv2.Merge(New Mat() {channels(0), channels(1), channels(2), alphaMask}, merged)
            Return merged
        Finally
            For Each c As Mat In channels
                c.Dispose()
            Next
        End Try
    End Function

    ''' <summary>Ghép chủ thể (theo alpha 0-255) lên nền trắng, tra ve Mat BGR.</summary>
    Private Shared Function ComposeOnWhite(srcBgr As Mat, alphaMask As Mat) As Mat
        Dim size As OpenCvSharp.Size = srcBgr.Size()

        Using alphaF As New Mat()
            alphaMask.ConvertTo(alphaF, MatType.CV_32FC1, 1.0 / 255.0)

            Using alpha3 As New Mat()
                Cv2.Merge(New Mat() {alphaF, alphaF, alphaF}, alpha3)

                Using invAlpha3 As New Mat()
                    Using ones As New Mat(size, MatType.CV_32FC3, Scalar.All(1.0))
                        Cv2.Subtract(ones, alpha3, invAlpha3)
                    End Using

                    Using srcF As New Mat()
                        srcBgr.ConvertTo(srcF, MatType.CV_32FC3)

                        Using fg As Mat = srcF.Mul(alpha3)
                            Using whiteBg As New Mat(size, MatType.CV_32FC3, Scalar.All(255.0))
                                Using bg As Mat = whiteBg.Mul(invAlpha3)
                                    Using sumF As New Mat()
                                        Cv2.Add(fg, bg, sumF)
                                        Dim result8U As New Mat()
                                        sumF.ConvertTo(result8U, MatType.CV_8UC3)
                                        Return result8U
                                    End Using
                                End Using
                            End Using
                        End Using
                    End Using
                End Using
            End Using
        End Using
    End Function
End Class
