"""Bounded read-only CLI metadata/IL inspection; never loads a target assembly."""
from pathlib import Path
import struct
import hashlib

CODED = {
    'ResolutionScope': (2, [0, 26, 35, 1]), 'TypeDefOrRef': (2, [2, 1, 27]),
    'HasConstant': (2, [4, 8, 23]),
    'HasCustomAttribute': (5, [6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43]),
    'HasFieldMarshal': (1, [4, 8]), 'HasDeclSecurity': (2, [2, 6, 32]),
    'MemberRefParent': (3, [2, 1, 26, 6, 27]), 'HasSemantics': (1, [20, 23]),
    'MethodDefOrRef': (1, [6, 10]), 'MemberForwarded': (1, [4, 6]),
    'Implementation': (2, [38, 35, 39]), 'CustomAttributeType': (3, [None, None, 6, 10, None]),
    'TypeOrMethodDef': (1, [2, 6]),
}
TABLES = {
    0: ['u2','s','g','g','g'], 1: ['ResolutionScope','s','s'],
    2: ['u4','s','s','TypeDefOrRef',4,6], 3:[4], 4:['u2','s','b'], 5:[6],
    6: ['u4','u2','u2','s','b',8], 7:[8], 8:['u2','u2','s'],
    9:[2,'TypeDefOrRef'], 10:['MemberRefParent','s','b'], 11:['u2','HasConstant','b'],
    12:['HasCustomAttribute','CustomAttributeType','b'], 13:['HasFieldMarshal','b'],
    14:['u2','HasDeclSecurity','b'], 15:['u2','u4',2], 16:['u4',4], 17:['b'],
    18:[2,20], 19:[20], 20:['u2','s','TypeDefOrRef'], 21:[2,23], 22:[23],
    23:['u2','s','b'], 24:['u2',6,'HasSemantics'], 25:[2,'MethodDefOrRef','MethodDefOrRef'],
    26:['s'], 27:['b'], 28:['u2','MemberForwarded','s',26], 29:['u4',4],
    30:['u4','u4'], 31:['u4'], 32:['u4','u2','u2','u2','u2','u4','b','s','s'],
    33:['u4'], 34:['u4','u4','u4'], 35:['u2','u2','u2','u2','u4','b','s','s','b'],
    36:['u4',35], 37:['u4','u4','u4',35], 38:['u4','s','b'],
    39:['u4','u4','s','s','Implementation'], 40:['u4','u4','s','Implementation'],
    41:[2,2], 42:['u2','u2','TypeOrMethodDef','s'], 43:['MethodDefOrRef','b'], 44:[42,'TypeDefOrRef'],
}

class Metadata:
    def __init__(self, path):
        self.path=Path(path); self.data=self.path.read_bytes()
        self.sha256=hashlib.sha256(self.data).hexdigest()
        pe=self.u(0x3c,4); assert self.data[pe:pe+4]==b'PE\0\0'
        sec_count=self.u(pe+6,2); opt_size=self.u(pe+20,2); opt=pe+24
        magic=self.u(opt,2); assert magic in (0x10b,0x20b)
        directory=opt+(96 if magic==0x10b else 112)
        sections=opt+opt_size; self.sections=[]
        for i in range(sec_count):
            p=sections+i*40
            self.sections.append((self.u(p+12,4),max(self.u(p+8,4),self.u(p+16,4)),self.u(p+20,4)))
        cli=self.offset(self.u(directory+14*8,4)); md=self.offset(self.u(cli+8,4))
        assert self.data[md:md+4]==b'BSJB'
        p=(md+16+self.u(md+12,4)+3)&~3; count=self.u(p+2,2);p+=4
        self.streams={}
        for _ in range(count):
            pos,size=self.u(p,4),self.u(p+4,4);p+=8
            end=self.data.index(0,p);name=self.data[p:end].decode('ascii');p=(end+4)&~3
            self.streams[name]=self.data[md+pos:md+pos+size]
        self.tab=self.streams.get('#~',self.streams.get('#-'));assert self.tab is not None
        self.heaps=self.tab[6];valid=int.from_bytes(self.tab[8:16],'little');p=24;self.counts={}
        for t in range(64):
            if valid>>t&1:self.counts[t]=int.from_bytes(self.tab[p:p+4],'little');p+=4
        self.positions={};self.widths={}
        for t,n in sorted(self.counts.items()):
            assert t in TABLES,('unknown table',t)
            widths=[self.width(k) for k in TABLES[t]];self.widths[t]=widths;self.positions[t]=p;p+=n*sum(widths)
        assert p<=len(self.tab)
        self.method_owner={};self.field_owner={};self.types={}
        for i in range(1,self.counts.get(2,0)+1):
            r=self.row(2,i);name=self.s(r[2])+'.'+self.s(r[1]);name=name.lstrip('.')
            self.types[name]=i
            nr=self.row(2,i+1) if i<self.counts[2] else [0,0,0,0,self.counts.get(4,0)+1,self.counts.get(6,0)+1]
            for x in range(r[4],nr[4]):self.field_owner[x]=name
            for x in range(r[5],nr[5]):self.method_owner[x]=name
    def u(self,p,n):return int.from_bytes(self.data[p:p+n],'little')
    def offset(self,rva):
        for base,size,raw in self.sections:
            if base<=rva<base+size:return raw+rva-base
        raise ValueError(('RVA outside sections',rva))
    def width(self,k):
        if isinstance(k,int):return 4 if self.counts.get(k,0)>=65536 else 2
        if k=='u2':return 2
        if k=='u4':return 4
        if k in ('s','g','b'):return 4 if self.heaps & {'s':1,'g':2,'b':4}[k] else 2
        bits,tables=CODED[k]
        return 4 if max((self.counts.get(t,0) for t in tables if t is not None),default=0)>=(1<<(16-bits)) else 2
    def row(self,t,i):
        assert 1<=i<=self.counts.get(t,0),(t,i)
        p=self.positions[t]+(i-1)*sum(self.widths[t]);vals=[]
        for width in self.widths[t]:vals.append(int.from_bytes(self.tab[p:p+width],'little'));p+=width
        return vals
    def s(self,i):
        if not i:return ''
        b=self.streams['#Strings'];return b[i:b.index(0,i)].decode('utf-8')
    @staticmethod
    def compressed(b,p):
        x=b[p]
        if x<0x80:return x,p+1
        if x<0xc0:return ((x&63)<<8)|b[p+1],p+2
        if x<0xe0:return ((x&31)<<24)|int.from_bytes(b[p+1:p+4],'big'),p+4
        raise ValueError('invalid compressed integer')
    def blob(self,i):
        if not i:return b''
        b=self.streams['#Blob'];n,p=self.compressed(b,i);return b[p:p+n]
    def coded(self,k,n):
        bits,tables=CODED[k];tag=n&((1<<bits)-1);return tables[tag],n>>bits
    def type_name(self,t,i):
        if not i:return None
        r=self.row(t,i)
        if t==2:return (self.s(r[2])+'.'+self.s(r[1])).lstrip('.')
        if t==1:return (self.s(r[2])+'.'+self.s(r[1])).lstrip('.')
        if t==27:return 'TypeSpec:'+self.blob(r[0]).hex()
        if t==26:return 'ModuleRef:'+self.s(r[0])
        if t==6:return self.method_owner[i]
        return f'table{t}:row{i}'
    def ref(self,token):
        t,i=token>>24,token&0xffffff
        if t==0x70:
            b=self.streams['#US'];n,p=self.compressed(b,i)
            return {'userString':b[p:p+n-1].decode('utf-16-le')}
        r=self.row(t,i)
        if t==6:return {'owner':self.method_owner[i],'name':self.s(r[3]),'signatureHex':self.blob(r[4]).hex()}
        if t==4:return {'owner':self.field_owner[i],'name':self.s(r[1]),'signatureHex':self.blob(r[2]).hex()}
        if t==10:
            parent=self.coded('MemberRefParent',r[0]);return {'owner':self.type_name(*parent),'name':self.s(r[1]),'signatureHex':self.blob(r[2]).hex()}
        if t in (1,2,27):return {'type':self.type_name(t,i)}
        if t==43:
            rt,ri=self.coded('MethodDefOrRef',r[0]);return {'method':self.ref(rt<<24|ri),'instantiationHex':self.blob(r[1]).hex()}
        return {'table':t,'row':i}
