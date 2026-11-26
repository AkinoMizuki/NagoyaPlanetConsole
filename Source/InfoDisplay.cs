using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NagoyaConsole;

// The VRChat StatusMonitor is the sole source of display values.
internal sealed record InfoFrame(string[] Values, int DirectionMask, int[] Digits, Color On, Color Off)
{
    internal static readonly int[] DefaultDigits = {5,2,2,5,5,5,5};
    internal static bool TryParse(string payload, out InfoFrame? frame)
    {
        frame=null;
        var p=payload.Split('|');
        if(p.Length!=4)return false;
        var v=p[0].Split(',');var d=p[2].Split(',');var c=p[3].Split(',');
        if(v.Length!=7||d.Length!=7||c.Length!=2)return false;
        if(!int.TryParse(p[1],NumberStyles.None,CultureInfo.InvariantCulture,out int mask)||mask<1||mask>511||(mask&1)==0)return false;
        int outer=mask&~1;if((outer&(outer-1))!=0)return false;
        var counts=new int[7];
        for(int i=0;i<7;i++)
        {
            if(v[i].Length==0||v[i].Length>16||v[i].Any(ch=>!(ch>='0'&&ch<='9')&&ch!='-'&&ch!='.'&&ch!=':'))return false;
            if(!int.TryParse(d[i],NumberStyles.None,CultureInfo.InvariantCulture,out counts[i])||counts[i]<1||counts[i]>16)return false;
        }
        if(!int.TryParse(c[0],NumberStyles.None,CultureInfo.InvariantCulture,out int on)||on<0||on>0xffffff||
           !int.TryParse(c[1],NumberStyles.None,CultureInfo.InvariantCulture,out int off)||off<0||off>0xffffff)return false;
        static Color Rgb(int n)=>Color.FromRgb((byte)(n>>16),(byte)(n>>8),(byte)n);
        frame=new(v,mask,counts,Rgb(on),Rgb(off));return true;
    }
    internal bool Same(InfoFrame? other)=>other!=null&&DirectionMask==other.DirectionMask&&On==other.On&&Off==other.Off&&Values.SequenceEqual(other.Values)&&Digits.SequenceEqual(other.Digits);
}

internal sealed class InfoDisplay:FrameworkElement
{
    private InfoFrame? frame;
    private double brightness=-1;
    private static readonly Brush Background=Brush("#09090B"), Bezel=Brush("#424246"), Label=Brush("#D3D1CE");
    private static Brush Brush(string hex){var b=(Brush)new BrushConverter().ConvertFromString(hex)!;b.Freeze();return b;}
    private static Brush Led(Color c,double light)
    {
        byte Scale(byte v)=>(byte)Math.Round(v*(0.12+0.88*Math.Clamp(light,0,1)));
        var b=new SolidColorBrush(Color.FromRgb(Scale(c.R),Scale(c.G),Scale(c.B)));b.Freeze();return b;
    }
    internal void SetState(InfoFrame? next,double light)
    {
        light=Math.Clamp(light,0,1);
        if(light==brightness&&(next==null?frame==null:next.Same(frame)))return;
        frame=next;brightness=light;InvalidateVisual();
    }
    private void Text(DrawingContext dc,string s,double x,double y,double size,Brush brush)
    {
        var t=new FormattedText(s,CultureInfo.GetCultureInfo("ja-JP"),FlowDirection.LeftToRight,new Typeface("Yu Gothic UI"),size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(t,new Point(x,y));
    }
    // Bit order is the original StatusMonitor material order: top, upper-left,
    // lower-left, bottom, lower-right, upper-right, middle.
    private static int Mask(char c)=>c switch{'0'=>63,'1'=>48,'2'=>109,'3'=>121,'4'=>114,'5'=>91,'6'=>95,'7'=>49,'8'=>127,'9'=>123,'-'=>64,_=>0};
    private static readonly Point[][] Segments={
        new Point[]{new(3,0),new(19,0),new(22,2),new(19,4),new(3,4),new(0,2)},
        new Point[]{new(0,4),new(3,6),new(3,16),new(1,19),new(-1,16),new(-1,6)},
        new Point[]{new(0,21),new(3,24),new(3,34),new(1,37),new(-1,34),new(-1,24)},
        new Point[]{new(3,36),new(19,36),new(22,38),new(19,40),new(3,40),new(0,38)},
        new Point[]{new(19,24),new(22,21),new(23,24),new(23,34),new(21,37),new(19,34)},
        new Point[]{new(19,6),new(22,3),new(23,6),new(23,16),new(21,19),new(19,16)},
        new Point[]{new(3,18),new(19,18),new(22,20),new(19,22),new(3,22),new(0,20)} };
    private static readonly Geometry[] GeometryCache=Segments.Select(points=>{
        var g=new StreamGeometry();using(var ctx=g.Open()){ctx.BeginFigure(points[0],true,true);ctx.PolyLineTo(points.Skip(1).ToArray(),true,false);}g.Freeze();return (Geometry)g;
    }).ToArray();
    private static void Digit(DrawingContext dc,char c,Brush on,Brush off)
    {
        // StatusMonitor assigns punctuation its own character slot with bit 0.
        if(c==':'){dc.DrawEllipse(on,null,new Point(11,13),2.6,2.6);dc.DrawEllipse(on,null,new Point(11,27),2.6,2.6);return;}
        if(c=='.'){dc.DrawEllipse(on,null,new Point(11,36),2.8,2.8);return;}
        int m=Mask(c);for(int i=0;i<7;i++)dc.DrawGeometry((m&(1<<i))!=0?on:off,null,GeometryCache[i]);
    }
    private void Field(DrawingContext dc,int index,string label,double x,double y,double w,Brush on,Brush off)
    {
        Text(dc,label,x+6,y,12,Label);
        dc.DrawRoundedRectangle(Background,new Pen(Bezel,1),new Rect(x,y+18,w,47),2,2);
        int n=frame?.Digits[index]??InfoFrame.DefaultDigits[index];
        string v=frame?.Values[index]??"";
        if(v.Length>n)v=v[^n..];v=v.PadLeft(n);
        double scale=Math.Min(.94,(w-14)/(n*27.0));double width=n*27*scale;
        dc.PushTransform(new TranslateTransform(x+(w-width)/2+scale*2,y+18+(47-40*scale)/2));
        dc.PushTransform(new ScaleTransform(scale,scale));
        foreach(char c in v){Digit(dc,c,on,off);dc.PushTransform(new TranslateTransform(27,0));}
        for(int i=0;i<n;i++)dc.Pop();dc.Pop();dc.Pop();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRoundedRectangle(PaintedMetal.Brush,new Pen(Bezel,1),new Rect(0,0,810,150),5,5);
        dc.DrawRoundedRectangle(Background,new Pen(Bezel,3),new Rect(8,7,794,136),3,3);
        var on=Led(frame?.On??Colors.Red,brightness);var off=Led(frame?.Off??Color.FromRgb(26,0,0),brightness);
        Field(dc,0,"年",20,9,150,on,off);Field(dc,1,"月",180,9,75,on,off);Field(dc,2,"日",265,9,75,on,off);Field(dc,3,"時刻",350,9,160,on,off);
        Field(dc,4,"緯度",20,75,150,on,off);Field(dc,5,"太陽高度",180,75,160,on,off);Field(dc,6,"地方恒星時",350,75,160,on,off);
        Text(dc,"方向",540,10,12,Label);
        dc.DrawRoundedRectangle(Background,new Pen(Bezel,1),new Rect(530,28,140,111),2,2);
        int mask=frame?.DirectionMask??0;
        for(int i=0;i<8;i++){double a=i*Math.PI/4;dc.DrawEllipse((mask&(1<<(i+1)))!=0?on:off,null,new Point(600+37*Math.Sin(a),83-37*Math.Cos(a)),6.5,6.5);}
        dc.DrawEllipse((mask&1)!=0?on:off,null,new Point(600,83),6.5,6.5);
        Text(dc,"INFO",684,37,16,Label);Text(dc,frame==null?"OFFLINE":"ACTIVE",684,67,14,frame==null?Brushes.Gray:Brushes.LightGreen);
    }
}
