using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;

namespace DuplicateFileFinder.Util
{
    public class AutoDisableImage : Image
    {
        protected override void OnRender(DrawingContext dc)
        {
            BitmapSource orgBmp = Source as BitmapSource;


            if (orgBmp == null)
                return;

            if (!IsEnabled)
            {
                // disable gray
                //bitmapSource = new FormatConvertedBitmap(bitmapSource, PixelFormats.Gray32Float, null, 0);

                byte[] orgPixels = new byte[orgBmp.PixelHeight * orgBmp.PixelWidth * 4];
                byte[] newPixels = new byte[orgPixels.Length];

                if (orgBmp.Format == PixelFormats.Bgra32)
                {
                    
                    orgBmp.CopyPixels(orgPixels, orgBmp.PixelWidth * 4, 0);
                    for (int i = 3; i < orgPixels.Length; i += 4)
                    {
                        int grayVal = ((int) orgPixels[i-3] + (int)orgPixels[i-2] + (int)orgPixels[i-3]);

                        if (grayVal != 0)
                            grayVal = grayVal / 3;
                        newPixels[i] = orgPixels[i];
                        newPixels[i - 3] = (byte) grayVal;
                        newPixels[i - 2] = (byte) grayVal;
                        newPixels[i - 1] = (byte) grayVal;
                    }

                    dc.DrawImage(
                        BitmapSource.Create(orgBmp.PixelWidth, orgBmp.PixelHeight, 96, 96, PixelFormats.Bgra32, null,
                            newPixels, orgBmp.PixelWidth * 4), new Rect(new Point(), RenderSize));
                }
            }
            else
            {
                dc.DrawImage(orgBmp, new Rect(new Point(), RenderSize));
            }
        }
    }
}
