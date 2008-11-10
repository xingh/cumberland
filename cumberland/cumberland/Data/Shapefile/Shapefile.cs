// Shapefile.cs
//
// Copyright (c) 2008 Scott Ellington and Authors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//

using System;
using System.Data;
using System.IO;
using System.Collections.Generic;
using System.Xml.Serialization;

using Cumberland;

namespace Cumberland.Data.Shapefile
{
	//TODO: Because this specification does not forbid consecutive points with identical coordinates, 
	// shapefile readers must handle such cases.

    public enum ShapeType
    {
        Null        = 0,
        Point       = 1,
        PolyLine    = 3,
        Polygon     = 5,
        MultiPoint  = 8,
        PointZ      = 11,
        PolyLineZ   = 13,
        PolygonZ    = 15,
        MultiPointZ = 18,
        PointM      = 21,
        PolyLineM   = 23,
        PolygonM    = 25,
        MultiPointM = 28,
        MultiPatch  = 31
    }
		
	
    public class Shapefile : IFeatureSource, IFileFeatureSource
	{				

#region Vars

		uint filelength;
		uint version;		
		bool isShapefileLoaded = false;
		bool isDbfLoaded = false;
		string currentField = null;
		DBaseIIIFile dbfFile = null;
		
#endregion
		
#region Properties
	
		[XmlIgnore]
		public ShapeType Shapetype {
			get {
				if (!isShapefileLoaded) LoadShapefile();
				
				return shapetype;
			}
		}
		ShapeType shapetype = ShapeType.Null;
		
		List<Feature> features = new List<Feature>();
		
		[XmlIgnore]
		public Rectangle Extents {
			get {
				if (!isShapefileLoaded) LoadShapefile();
				
				return listedExtents;
			}
		}

		[XmlIgnore]
		public uint Version {
			get {
				return version;
			}
		}

		[XmlIgnore]
		public Cumberland.Data.FeatureType SourceFeatureType {
			get {
				if (!isShapefileLoaded) LoadShapefile();
				
				return featureType;
			}
		}

		public string FilePath {
			get {
				return fileName;
			}
			set {
				fileName = value;
				
				// TODO: open?
			}
		}
		
		FeatureType featureType;
		
		Rectangle listedExtents = new Cumberland.Rectangle();

		string fileName;
		
#endregion

#region ctor
		
		public Shapefile()
		{
		}
		
        public Shapefile(string fname)
        {
			fileName = fname;
			LoadShapefile();
		}

		
#endregion
	
#region Helper methods		
		
	
        uint FlipEndian(uint iin)
        {
            byte[] temp = BitConverter.GetBytes(iin);
            Array.Reverse(temp);
            return BitConverter.ToUInt32(temp,0);
        }

        void ReadFileHeader(BinaryReader stream)
        {
            // grab the File Code
            uint filecode = FlipEndian(stream.ReadUInt32());

            if (filecode != 9994)
            {
				throw new InvalidDataException("This file does not appear to be a shapefile");
            }

            // the next 20 bytes are junk
            byte[] junk = new byte[20];
            stream.Read(junk,0,junk.Length);

            // grab the file length
            filelength = FlipEndian(stream.ReadUInt32());

            // get version
            version = stream.ReadUInt32();

            // get shape type
            shapetype = (ShapeType) stream.ReadUInt32();
			
			if (shapetype == ShapeType.Point)
			{
				featureType = FeatureType.Point;
			}
			else if (shapetype == ShapeType.Polygon)
			{
				featureType = FeatureType.Polygon;
			}
			else if (shapetype == ShapeType.PolyLine)
			{
				featureType = FeatureType.Polyline;
			}
			else
			{
				throw new NotSupportedException("Unsuppored shapefile type: " + shapetype);
			}

            // get extents
            double xmin, ymin, xmax, ymax; // zmin, mmin, zmax, mmax;
            xmin = stream.ReadDouble();
            ymin = stream.ReadDouble();
            xmax = stream.ReadDouble();
            ymax = stream.ReadDouble();
            stream.ReadDouble(); // zmin =
            stream.ReadDouble(); // zmax =
            stream.ReadDouble(); // mmin = 
            stream.ReadDouble(); // mmax =
			
            listedExtents = new Rectangle(xmin, ymin, xmax, ymax);
        }

		void ReadShapeRecords(BinaryReader stream)
		{
		   	uint loc = 50;  // current position in file
		   
			while (loc < filelength)
			{
				uint recordNum = FlipEndian(stream.ReadUInt32());
				uint recordLen = FlipEndian(stream.ReadUInt32());
				uint recordShp = stream.ReadUInt32();
				
				// Chop off 32 bit from our remaining record because we read the shape type
				uint dataleft = recordLen - 2;
				switch (recordShp)
				{
				   	case 0:
					   	// Null Object, nothing to read in
					   	//Console.WriteLine("INFO: Null Shape Found");
						break;
					case 1:
						// Read in Point object
						Point p = new Point(stream.ReadDouble(), stream.ReadDouble());
						p.Id = recordNum;
						features.Add(p);
						break;
					case 3:
						// Read in PolyLine object
					   	PolyLine po = GetPolyLine(stream, dataleft);
						po.Id = recordNum;
						features.Add(po);						
						break;
					case 5:
						// Read in Polygon object
					   	Polygon pol = GetPolygon(stream, dataleft);
						pol.Id = recordNum;
						features.Add(pol);						
						break;
					default:
						// Anything unsupported gets dumped
						//Console.WriteLine("INFO: Unsupported Shape Type");
						byte[] data = new byte[dataleft * 2];
						stream.Read(data, 0, data.Length);
						break;
				}
				
				// log distance we have traversed in file: record length + header size
				loc += recordLen + 4;
			}
		}
		
		Polygon GetPolygon(BinaryReader stream, uint dlen)
		{
			//Polygons stored in a shapefile must be clean. A clean polygon is one that
			// 1.  Has no self-intersections. This means that a segment belonging to one ring may
			//     not intersect a segment belonging to another ring. The rings of a polygon can
			//     touch each other at vertices but not along segments. Colinear segments are
			//     considered intersecting.
			// 2.  Has the inside of the polygon on the "correct" side of the line that defines it. The
			//     neighborhood to the right of an observer walking along the ring in vertex order is
			//     the inside of the polygon. Vertices for a single, ringed polygon are, therefore,
			//     always in clockwise order. Rings defining holes in these polygons have a
			//     counterclockwise orientation. "Dirty" polygons occur when the rings that define
			//     holes in the polygon also go clockwise, which causes overlapping interiors.
			
			// The order of rings in the points array is not significant.
			
			stream.ReadDouble(); // double xmin = 
			stream.ReadDouble(); // double ymin = 
			stream.ReadDouble(); // double xmax = 
			stream.ReadDouble(); // double ymax = 

			uint numParts = stream.ReadUInt32();
			uint numPoints = stream.ReadUInt32();

			uint[] parts = new uint[numParts];
			int ii;
			for (ii=0; ii < numParts; ii++)
				parts[ii] = stream.ReadUInt32();

			Ring[] rings = new Ring[numParts];
			for (ii=0; ii < rings.Length; ii++)
			   	rings[ii] = new Ring();
			
			//Polygon po = new Polygon(xmin, ymin, xmax, ymax);
			Polygon po = new Polygon();
			
			ii=0;
			for (int jj=0; jj < numPoints; jj++)
			{
				if (ii < parts.Length-1)
				{
				   	if (parts[ii+1] == jj)
					{
						po.Rings.Add(rings[ii]);

					   	ii++;
					}
				}
				
				Point p = new Point(stream.ReadDouble(), stream.ReadDouble());
				rings[ii].Points.Add(p);				
			}

			po.Rings.Add(rings[ii]);
			
			return po;
		}
		
		PolyLine GetPolyLine(BinaryReader stream, uint dlen)
		{
			stream.ReadDouble(); // double xmin = 
			stream.ReadDouble(); // double ymin = 
			stream.ReadDouble(); // double xmax = 
			stream.ReadDouble(); // double ymax = 

			uint numParts = stream.ReadUInt32();
			uint numPoints = stream.ReadUInt32();

			uint[] parts = new uint[numParts];
			int ii;
			for (ii=0; ii < numParts; ii++)
				parts[ii] = stream.ReadUInt32();

			Line[] lines = new Line[numParts];
			for (ii=0; ii < lines.Length; ii++)
			   	lines[ii] = new Line();
			
			//PolyLine po = new PolyLine(xmin, ymin, xmax, ymax);
			PolyLine po = new PolyLine();

			ii=0;
			for (int jj=0; jj < numPoints; jj++)
			{
				if (ii < parts.Length-1)
				   	if (parts[ii+1] == jj)
					{
						po.Lines.Add(lines[ii]);
					   	ii++;
					}
				Point p = new Point(stream.ReadDouble(), stream.ReadDouble());
				lines[ii].Points.Add(p);				
			}
			po.Lines.Add(lines[ii]);
			
			return po;
		}

		void LoadShapefile()
		{
			if (isShapefileLoaded) return;
			
			using (FileStream file = new FileStream(fileName, FileMode.Open, FileAccess.Read))
			{
           		BinaryReader str = new BinaryReader(file);
	            str.BaseStream.Seek(0, SeekOrigin.Begin); 
				
	            ReadFileHeader(str);
				ReadShapeRecords(str);
			}
			
			isShapefileLoaded = true;
		}
		
		void LoadDbf()
		{
			if (isDbfLoaded) return;
			
			dbfFile = new DBaseIIIFile(Path.GetDirectoryName(fileName) + 
			                                    Path.DirectorySeparatorChar + 
			                                    Path.GetFileNameWithoutExtension(fileName) + 
			                                    ".dbf");
			
			isDbfLoaded = true;
		}
		
#endregion
		
#region IFeatureSource methods
		
		public List<Feature> GetFeatures(string themeField)
		{
			return GetFeatures(new Rectangle(), themeField);
		}
		
		public List<Feature> GetFeatures()
		{
			return GetFeatures(new Rectangle());
		}
		
		public List<Cumberland.Feature> GetFeatures (Cumberland.Rectangle rectangle)
		{
			return GetFeatures(rectangle, null);
		}
		
		public List<Feature> GetFeatures(Cumberland.Rectangle rectangle, string themeField)
		{
			if (!isShapefileLoaded) LoadShapefile();
			
			if (themeField != null && 
			    !isDbfLoaded && 
			    themeField != currentField)
			{
				LoadDbf();
				
				// find our theme field in the dbf
				int fieldIndex = -1;
				for (int ii=0; ii<dbfFile.Records.Columns.Count; ii++)
				{
					if (dbfFile.Records.Columns[ii].ColumnName == themeField)
					{
						fieldIndex = ii;
						break;
					}
				}
				
				if (fieldIndex == -1)
				{
					throw new ArgumentException("Theme field not found in dbf", "themeField");
				}
				
				// updates the features
				for (int ii=0; ii < features.Count; ii++)
				{
					features[ii].ThemeFieldValue = dbfFile.Records.Rows[ii][fieldIndex].ToString();
				}
				
				currentField = themeField;
			}
			
			// we've got no spatial index 
			return features;
		}
		
#endregion
						
    }
}

