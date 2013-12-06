﻿#region License
/**
 * Copyright (c) 2013 Robert Rouhani <robert.rouhani@gmail.com> and other contributors (see CONTRIBUTORS file).
 * Licensed under the MIT License - https://raw.github.com/Robmaister/SharpNav/master/LICENSE
 */
#endregion

using System;
using System.Collections.Generic;
using SharpNav.Geometry;

namespace SharpNav
{
	public class NavMeshBuilder
	{
		//constants
		private const int MAX_VERTS_PER_POLYGON = 6;

		private const int NAVMESH_MAGIC = 'D' << 24 | 'N' << 16 | 'A' << 8 | 'V';
		private const int NAVMESH_VERSION = 7;

		private const int EXT_LINK = 0x8000; //entity links to external entity

		private const int OFFMESH_CON_BIDIR = 1; //bidirectional

		private const int DT_POLTYPE_GROUND = 0;
		private const int DT_POLTYPE_OFFMESH_CONNECTION = 1;

		//convert NavMesh and NavMeshDetail into a different data structure suited for pathfinding
		private MeshHeader header;
		private Vector3[] navVerts;
		private Poly[] navPolys;
		private PolyDetail[] navDMeshes;
		private Vector3[] navDVerts;
		private NavMeshDetail.TrisInfo[] navDTris;
		private OffMeshConnection[] offMeshCons;

		public NavMeshBuilder(NavMeshCreateParams parameters, int[] outData, ref int outDataSize)
		{
			if (parameters.numVertsPerPoly > MAX_VERTS_PER_POLYGON)
				return;
			if (parameters.vertCount >= 0xffff)
				return;
			if (parameters.vertCount == 0 || parameters.verts.Length == 0)
				return;
			if (parameters.polyCount == 0 || parameters.polys.Length == 0)
				return;

			int nvp = parameters.numVertsPerPoly;

			//classify off-mesh connection points
			int[] offMeshConClass = new int[parameters.offMeshConCount * 2];
			int storedOffMeshConCount = 0;
			int offMeshConLinkCount = 0;

			if (parameters.offMeshConCount > 0)
			{
				//find height bounds
				float hmin = float.MaxValue;
				float hmax = -float.MinValue;

				if (parameters.detailVerts.Length != 0 && parameters.detailVertsCount != 0)
				{
					for (int i = 0; i < parameters.detailVertsCount; i++)
					{
						float h = parameters.detailVerts[i].Y;
						hmin = Math.Min(hmin, h);
						hmax = Math.Max(hmax, h);
					}
				}
				else
				{
					for (int i = 0; i < parameters.vertCount; i++)
					{
						Vector3 iv = parameters.verts[i];
						float h = parameters.bounds.Min.Y + iv.Y * parameters.cellHeight;
						hmin = Math.Min(hmin, h);
						hmax = Math.Max(hmax, h);
					}
				}
				hmin -= parameters.walkableClimb;
				hmax += parameters.walkableClimb;
				BBox3 bounds = parameters.bounds;
				bounds.Min.Y = hmin;
				bounds.Max.Y = hmax;

				for (int i = 0; i < parameters.offMeshConCount; i++)
				{
					Vector3 p0 = parameters.offMeshConVerts[i * 2 + 0];
					Vector3 p1 = parameters.offMeshConVerts[i * 2 + 1];

					offMeshConClass[i * 2 + 0] = classifyOffMeshPoint(p0, bounds);
					offMeshConClass[i * 2 + 1] = classifyOffMeshPoint(p1, bounds);

					//off-mesh start position isn't touching mesh
					if (offMeshConClass[i * 2 + 0] == 0xff)
					{
						if (p0.Y < bounds.Min.Y || p0.Y > bounds.Max.Y)
							offMeshConClass[i * 2 + 0] = 0;
					}

					//count number of links to allocate
					if (offMeshConClass[i * 2 + 0] == 0xff)
						offMeshConLinkCount++;
					if (offMeshConClass[i * 2 + 1] == 0xff)
						offMeshConLinkCount++;

					if (offMeshConClass[i * 2 + 0] == 0xff)
						storedOffMeshConCount++;
				}
			}

			//off-mesh connections stored as polygons, adjust values
			int totPolyCount = parameters.polyCount + storedOffMeshConCount;
			int totVertCount = parameters.vertCount + storedOffMeshConCount * 2;

			//find portal edges
			int edgeCount = 0;
			int portalCount = 0;
			for (int i = 0; i < parameters.polyCount; i++)
			{
				NavMesh.Polygon p = parameters.polys[i];
				for (int j = 0; j < nvp; j++)
				{
					if (p.Vertices[j] == NavMesh.MESH_NULL_IDX)
						break;

					edgeCount++;
					
					if ((p.ExtraInfo[j] & 0x8000) != 0)
					{
						int dir = p.ExtraInfo[j] & 0xf;
						if (dir != 0xf)
							portalCount++;
					}
				}
			}

			int maxLinkCount = edgeCount + portalCount * 2 + offMeshConLinkCount * 2;

			//find unique detail vertices
			int uniqueDetailVertCount = 0;
			int detailTriCount = 0;
			if (parameters.detailMeshes.Length != 0)
			{
				detailTriCount = parameters.detailTriCount;
				for (int i = 0; i < parameters.polyCount; i++)
				{
					NavMesh.Polygon p = parameters.polys[i];
					int ndv = parameters.detailMeshes[i].NewNumVerts;
					int nv = 0;
					for (int j = 0; j < nvp; j++)
					{
						if (p.Vertices[j] == NavMesh.MESH_NULL_IDX)
							break;

						nv++;
					}
					ndv -= nv;
					uniqueDetailVertCount += ndv;
				}
			}
			else
			{
				uniqueDetailVertCount = 0;
				detailTriCount = 0;
				for (int i = 0; i < parameters.polyCount; i++)
				{
					NavMesh.Polygon p = parameters.polys[i];
					int nv = 0;
					for (int j = 0; j < nvp; j++)
					{
						if (p.Vertices[j] == NavMesh.MESH_NULL_IDX)
							break;

						nv++;
					}
					uniqueDetailVertCount += nv - 2;
				}
			}

			//store header
			header.magic = NAVMESH_MAGIC;
			header.version = NAVMESH_VERSION;
			header.x = parameters.tileX;
			header.y = parameters.tileY;
			header.layer = parameters.tileLayer;
			header.userId = parameters.userId;
			header.polyCount = totPolyCount;
			header.vertCount = totVertCount;
			header.maxLinkCount = maxLinkCount;
			header.bounds = parameters.bounds;
			header.detailMeshCount = parameters.polyCount;
			header.detailVertCount = uniqueDetailVertCount;
			header.detailTriCount = detailTriCount;
			header.bvQuantFactor = 1.0f / parameters.cellSize;
			header.offMeshBase = parameters.polyCount;
			header.walkableHeight = parameters.walkableHeight;
			header.walkableRadius = parameters.walkableRadius;
			header.walkableClimb = parameters.walkableClimb;
			header.offMeshConCount = storedOffMeshConCount;
			header.bvNodeCount = parameters.buildBvTree ? parameters.polyCount * 2 : 0;

			int offMeshVertsBase = parameters.vertCount;
			int offMeshPolyBase = parameters.polyCount;

			//store vertices
 			navVerts = new Vector3[totVertCount];
			for (int i = 0; i < parameters.vertCount; i++)
			{
				Vector3 iv = parameters.verts[i];
				navVerts[i].X = parameters.bounds.Min.X + iv.X * parameters.cellSize;
				navVerts[i].Y = parameters.bounds.Min.Y + iv.Y * parameters.cellHeight;
				navVerts[i].Z = parameters.bounds.Min.Z + iv.Z * parameters.cellSize;
			}
			//off-mesh link vertices
			int n = 0;
			for (int i = 0; i < parameters.offMeshConCount; i++)
			{
				//only store connections which start from this tile
				if (offMeshConClass[i * 2 + 0] == 0xff)
				{
					navVerts[offMeshVertsBase + (n * 2 + 0)] = parameters.offMeshConVerts[i * 2 + 0];
					navVerts[offMeshVertsBase + (n * 2 + 1)] = parameters.offMeshConVerts[i * 2 + 1];
					n++;
				}
			}

			//store polygons
			navPolys = new Poly[totPolyCount];
			for (int i = 0; i < parameters.polyCount; i++)
			{
				navPolys[i].vertCount = 0;
				navPolys[i].flags = parameters.polyFlags[i];
				navPolys[i].setArea((int)parameters.polyAreas[i]);
				navPolys[i].setType(DT_POLTYPE_GROUND);

				navPolys[i].verts = new int[nvp];
				navPolys[i].neis = new int[nvp];
				for (int j = 0; j < nvp; j++)
				{
					if (parameters.polys[i].Vertices[j] == NavMesh.MESH_NULL_IDX)
						break;

					navPolys[i].verts[j] = parameters.polys[i].Vertices[j];
					if ((parameters.polys[i].ExtraInfo[j] & 0x8000) != 0)
					{
						//border or portal edge
						int dir = parameters.polys[i].ExtraInfo[j] & 0xf;
						if (dir == 0xf) //border
							navPolys[i].neis[j] = 0;
						else if (dir == 0) //portal x-
							navPolys[i].neis[j] = EXT_LINK | 4;
						else if (dir == 1) //portal z+
							navPolys[i].neis[j] = EXT_LINK | 2;
						else if (dir == 2) //portal x+
							navPolys[i].neis[j] = EXT_LINK | 0;
						else if (dir == 3) //portal z-
							navPolys[i].neis[j] = EXT_LINK | 6;
					}
					else
					{
						//normal connection
						navPolys[i].neis[j] = parameters.polys[i].ExtraInfo[j] + 1;
					}

					navPolys[i].vertCount++;
				}
			}
			//off-mesh connection vertices
			n = 0;
			for (int i = 0; i < parameters.offMeshConCount; i++)
			{
				//only store connections which start from this tile
				if (offMeshConClass[i * 2 + 0] == 0xff)
				{
					navPolys[offMeshPolyBase + n].vertCount = 2;
					navPolys[offMeshPolyBase + n].verts = new int[nvp];
					navPolys[offMeshPolyBase + n].verts[0] = offMeshVertsBase + (n * 2 + 0);
					navPolys[offMeshPolyBase + n].verts[1] = offMeshVertsBase + (n * 2 + 1);
					navPolys[offMeshPolyBase + n].flags = parameters.offMeshConFlags[i];
					navPolys[offMeshPolyBase + n].setArea(parameters.offMeshConAreas[i]);
					navPolys[offMeshPolyBase + n].setType(DT_POLTYPE_OFFMESH_CONNECTION);
					n++;
				}
			}

			//store detail meshes and vertices
			navDMeshes = new PolyDetail[parameters.polyCount];
			navDVerts = new Vector3[uniqueDetailVertCount];
			navDTris = new NavMeshDetail.TrisInfo[detailTriCount];
			if (parameters.detailMeshes.Length != 0)
			{
				int vbase = 0;
				for (int i = 0; i < parameters.polyCount; i++)
				{
					int vb = parameters.detailMeshes[i].OldNumVerts;
					int ndv = parameters.detailMeshes[i].NewNumVerts;
					int nv = navPolys[i].vertCount;
					navDMeshes[i].vertBase = (uint)vbase;
					navDMeshes[i].vertCount = ndv - nv;
					navDMeshes[i].triBase = (uint)parameters.detailMeshes[i].OldNumTris;
					navDMeshes[i].triCount = parameters.detailMeshes[i].NewNumTris;

					//copy vertices except for first 'nv' verts which are equal to nav poly verts
					if (ndv - nv > 0)
					{
						for (int j = 0; j < ndv - nv; j++)
						{
							navDVerts[vbase + j] = parameters.detailVerts[vb + nv + j];
						}
						vbase += ndv - nv;
					}
				}

				//store triangles
				for (int j = 0; j < parameters.detailTriCount; j++)
					navDTris[j] = parameters.detailTris[j];
			}
			else
			{
				//create dummy detail mesh by triangulating polys
				int tbase = 0;
				for (int i = 0; i < parameters.polyCount; i++)
				{
					int nv = navPolys[i].vertCount;
					navDMeshes[i].vertBase = 0;
					navDMeshes[i].vertCount = 0;
					navDMeshes[i].triBase = (uint)tbase;
					navDMeshes[i].triCount = nv - 2;

					//triangulate polygon
					for (int j = 2; j < nv; j++)
					{
						navDTris[tbase].VertexHash[0] = 0;
						navDTris[tbase].VertexHash[1] = j - 1;
						navDTris[tbase].VertexHash[2] = j;

						//bit for each edge that belongs to the poly boundary
						navDTris[tbase].Flag = 1 << 2;
						if (j == 2) 
							navDTris[tbase].Flag |= (1 << 0);
						if (j == nv - 1)
							navDTris[tbase].Flag |= (1 << 4);
						
						tbase++;
					}
				}
			}

			//store and create BV tree
			if (parameters.buildBvTree)
			{
				//build tree
				//....
			}

			//store off-mesh connections
			n = 0;
			offMeshCons = new OffMeshConnection[storedOffMeshConCount];
			for (int i = 0; i < parameters.offMeshConCount; i++)
			{
				//only store connections which start from this tile
				if (offMeshConClass[i * 2 + 0] == 0xff)
				{
					offMeshCons[n].poly = offMeshPolyBase + n;

					//copy connection end points
					offMeshCons[n].pos = new Vector3[2];
					offMeshCons[n].pos[0] = parameters.offMeshConVerts[i * 2 + 0];
					offMeshCons[n].pos[1] = parameters.offMeshConVerts[i * 2 + 1];

					offMeshCons[n].radius = parameters.offMeshConRadii[i];
					offMeshCons[n].flags = (parameters.offMeshConDir[i] != 0) ? OFFMESH_CON_BIDIR : 0;
					offMeshCons[n].side = offMeshConClass[i * 2 + 1];
					if (parameters.offMeshConUserID.Length != 0)
						offMeshCons[n].userId = parameters.offMeshConUserID[i];

					n++;
				}
			}
		}

		public int classifyOffMeshPoint(Vector3 pt, BBox3 bounds)
		{
			const int XP = 1 << 0; //x plus
			const int ZP = 1 << 1; //z plus 
			const int XM = 1 << 2; //x minus
			const int ZM = 1 << 3; //z minus

			int outcode = 0;
			outcode |= (pt.X >= bounds.Max.X) ? XP : 0;
			outcode |= (pt.Z >= bounds.Max.Z) ? ZP : 0;
			outcode |= (pt.X < bounds.Min.X) ? XM : 0;
			outcode |= (pt.Z < bounds.Min.Z) ? ZM : 0;

			switch (outcode)
			{
				case XP:
					return 0;

				case XP | ZP:
					return 1;

				case ZP:
					return 2;

				case XM | ZP:
					return 3;

				case XM:
					return 4;

				case XM | ZM:
					return 5;

				case ZM:
					return 6;

				case XP | ZM:
					return 7;
			}

			return 0xff;
		}

		public class MeshHeader
		{
			public int magic; //tile magic number (used to identify data format)
			public int version;
			public int x;
			public int y;
			public int layer;
			public uint userId;
			public int polyCount;
			public int vertCount;
			public int maxLinkCount;
			public int detailMeshCount;

			public int detailVertCount;

			public int detailTriCount;
			public int bvNodeCount;
			public int offMeshConCount;
			public int offMeshBase; //index of first polygon which is off-mesh connection
			public float walkableHeight;
			public float walkableRadius;
			public float walkableClimb;
			public BBox3 bounds;

			public float bvQuantFactor; //bounding volume quantization facto
		}

		public class Poly
		{
			public uint firstLink; //index to first link in linked list
			public int[] verts; //indices of polygon's vertices
			public int[] neis; //packed data representing neighbor polygons references and flags for each edge
			public int flags; //user defined polygon flags
			public int vertCount;
			public int areaAndtype; //bit packed area id and polygon type

			public void setArea(int a)
			{
				areaAndtype = (areaAndtype & 0xc0) | (a & 0x3f); 
			}

			public void setType(int t)
			{
				areaAndtype = (areaAndtype & 0x3f) | (t << 6); 
			}
		}

		public class PolyDetail
		{
			public uint vertBase; //offset of vertices in some array
			public uint triBase; //offset of triangles in some array
			public int vertCount;
			public int triCount;
		}

		public class OffMeshConnection
		{
			public Vector3[] pos; //the endpoints of the connection
			public float radius;
			public int poly;
			public int flags; //assigned flag from Poly
			public int side; //endpoint side
			public uint userId; //id of offmesh connection
		}
	}
}
