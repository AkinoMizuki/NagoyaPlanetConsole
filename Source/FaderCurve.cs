using System.Globalization;

namespace NagoyaConsole;

// A strictly increasing, invertible calibration from oriented MIDI position to
// normalized console value. No device-specific audio or MIDI protocol assumptions.
internal sealed class FaderCurve
{
    public const string PercentExample = "0=0\n25=5\n50=25\n75=65\n100=100";
    public const string DbExample = "0=-inf\n5=-60\n15=-40\n30=-20\n45=-10\n60=-5\n75=0\n90=5\n100=10";
    readonly double[] x, y, db;
    readonly bool logarithmic;
    readonly double maxDb;
    FaderCurve(double[] x,double[] y,double[] db,bool logarithmic,double maxDb)
    {this.x=x;this.y=y;this.db=db;this.logarithmic=logarithmic;this.maxDb=maxDb;}
    public static bool TryCreate(int mode,string? text,out FaderCurve? curve,out string error)
    {
        curve=null;error="";
        if(mode==0)return true;
        if(mode<1||mode>2||string.IsNullOrWhiteSpace(text)||text.Length>4096){error="カーブ設定を確認してください";return false;}
        var xs=new List<double>();var ys=new List<double>();
        foreach(var line in text.Replace("\r", "").Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            var parts=line.Split('=',StringSplitOptions.TrimEntries);
            if(parts.Length!=2||!double.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out double a)||!double.IsFinite(a)||a<0||a>100){error="各行は MIDI位置% = 値 の形式です";return false;}
            double b;
            if(mode==2&&xs.Count==0&&(parts[1]=="-inf"||parts[1]=="-∞"||parts[1]=="∞"))b=double.NegativeInfinity;
            else if(!double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out b)||!double.IsFinite(b)||(mode==1?(b<0||b>100):(b< -120||b>24))){error=mode==1?"制御値は0～100%です":"dBは-120～+24、先頭のみ-infです";return false;}
            if(xs.Count>0&&(a<=xs[^1]||b<=ys[^1])){error="位置と値はどちらも昇順・重複なしにしてください";return false;}
            xs.Add(a);ys.Add(b);
            if(xs.Count>64){error="最大64点です";return false;}
        }
        if(xs.Count<2||xs[0]!=0||xs[^1]!=100||(mode==1?(ys[0]!=0||ys[^1]!=100):!double.IsNegativeInfinity(ys[0]))){error="始点0%、終点100%。値は%モードで0～100、dBモードで-infから開始してください";return false;}
        double max=ys[^1];
        var values=ys.Select(v=>mode==1?v/100:double.IsNegativeInfinity(v)?0:Math.Pow(10,(v-max)/20)).ToArray();
        curve=new FaderCurve(xs.Select(v=>v/100).ToArray(),values,ys.ToArray(),mode==2,max);
        return true;
    }
    static int Segment(double[] a,double v){int i=Array.BinarySearch(a,v);return i>=0?Math.Min(i,a.Length-2):Math.Clamp(~i-1,0,a.Length-2);}
    static double Mix(double a,double b,double t)=>a+(b-a)*t;
    public double Forward(double position)
    {
        position=Math.Clamp(position,0,1);if(position==0||position==1)return position;
        int i=Segment(x,position);double t=(position-x[i])/(x[i+1]-x[i]);
        // Between mute and first finite dB knot interpolate amplitude, not infinity.
        return logarithmic&&i>0?Math.Pow(10,(Mix(db[i],db[i+1],t)-maxDb)/20):Mix(y[i],y[i+1],t);
    }
    public double Inverse(double value)
    {
        value=Math.Clamp(value,0,1);if(value==0||value==1)return value;
        int i=Segment(y,value);double t=logarithmic&&i>0?(20*Math.Log10(value)+maxDb-db[i])/(db[i+1]-db[i]):(value-y[i])/(y[i+1]-y[i]);
        return Math.Clamp(Mix(x[i],x[i+1],t),0,1);
    }
}
