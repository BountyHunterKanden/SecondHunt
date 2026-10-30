import sys,glob,os
from PIL import Image, ImageDraw
d=sys.argv[1]; out=sys.argv[2]; pat=sys.argv[3] if len(sys.argv)>3 else 'f*.png'
fs=sorted(glob.glob(os.path.join(d,pat)))
ims=[Image.open(f).convert('RGB') for f in fs]
w,h=ims[0].size; cols=min(10,len(ims)); rows=(len(ims)+cols-1)//cols
sheet=Image.new('RGB',(cols*w,rows*(h+14)),(40,0,40))
dr=ImageDraw.Draw(sheet)
for i,(f,im) in enumerate(zip(fs,ims)):
    x=(i%cols)*w; y=(i//cols)*(h+14)
    sheet.paste(im,(x,y+14)); dr.text((x+2,y),os.path.basename(f),fill=(255,255,0))
sheet.save(out); print(w,h,len(ims),sheet.size)
