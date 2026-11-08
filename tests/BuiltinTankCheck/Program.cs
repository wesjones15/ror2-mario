using System;
using MarioTest;
static class Program
{
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    static int Count(byte[] pixels,byte r,byte g,byte b){int n=0;for(int p=0;p<pixels.Length;p+=4)if(pixels[p]==r&&pixels[p+1]==g&&pixels[p+2]==b&&pixels[p+3]>0)n++;return n;}
    static void Main(){
        var empty=BuiltinTankImage.Render(0,0);var half=BuiltinTankImage.Render(.5f,0);var full=BuiltinTankImage.Render(1,0);
        int Blue(byte[] p)=>Count(p,25,145,255);
        Check(Blue(empty)==0&&Blue(half)>0&&Blue(half)<Blue(full),"water rectangle does not reflect tank level");
        int last=-1;
        foreach(float charge in new[]{0f,.25f,.5f,1f}){
            var pixels=BuiltinTankImage.Render(.5f,charge);int orange=Count(pixels,255,155,25);
            Check(orange>last,"Rocket charge does not fill the circle progressively");last=orange;
            Check(Blue(pixels)==Blue(half),"charge overwrote the water meter");
        }
        Check(empty[3]==0&&full[3]==0,"circle is opaque outside its bounds");
        Check(BuiltinTankImage.Letter(2)=="R"&&BuiltinTankImage.Letter(5)=="H"&&BuiltinTankImage.Letter(0)=="N","native nozzle IDs do not map to the fallback letters");
        Console.WriteLine("PASS: empty/half/full water, independent ascending Rocket charge, transparent bounds and nozzle letters");
    }
}
