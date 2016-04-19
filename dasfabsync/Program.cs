using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dasfabsync
{
    class Program
    {
        static void Main(string[] args)
        {
            MongoUpload mup = new MongoUpload();
            mup.start();
        }
    }
}
