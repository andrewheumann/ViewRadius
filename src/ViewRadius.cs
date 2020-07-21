using Elements;
using Elements.Geometry;
using Elements.Geometry.Solids;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using solids = Elements.Geometry.Solids;

namespace ViewRadius
{
    public static class ViewRadius
    {
        /// <summary>
        /// The ViewRadius function.
        /// </summary>
        /// <param name="model">The input model.</param>
        /// <param name="input">The arguments to the execution.</param>
        /// <returns>A ViewRadiusOutputs instance containing computed results and the model with any new elements.</returns>
        public static ViewRadiusOutputs Execute(Dictionary<string, Model> inputModels, ViewRadiusInputs input)
        {
            inputModels.TryGetValue("Envelope", out Model envelopeModel);
            inputModels.TryGetValue("location", out Model contextBuildingsModel);
            if (envelopeModel == null)
            {
                throw new Exception("Unable to find envelope model.");
            }
            if (contextBuildingsModel == null)
            {
                throw new Exception("Unable to find Location model.");
            }
            var allEnvelopes = envelopeModel.AllElementsOfType<Envelope>();
            var allContextBuildings = contextBuildingsModel.AllElementsOfType<Mass>();
            if (!allEnvelopes.Any())
            {
                throw new Exception("No envelopes in model.");
            }
            if (!allContextBuildings.Any())
            {
                throw new Exception("No context buildings in model.");
            }
            var height = input.Height;
            var envelopesAtHeight = allEnvelopes.Where(env => height > env.Elevation && height < env.Height + env.Elevation);

            var model = new Model();
            int rayCount = 170;

            var allFaces = allContextBuildings.SelectMany(b => b.Representation.SolidOperations.Select(s => s.Solid.Faces));
            var maxTotalScore = 0.0;
            var totalScore = 0.0;
            foreach (var envelope in envelopesAtHeight)
            {
                var perimeter = envelope.Profile.Perimeter;
                var vertexAverage = perimeter.Vertices.Average();
                var minRadius = perimeter.Vertices.Select(v => v.DistanceTo(vertexAverage)).Max();
                var circle = Polygon.Circle(minRadius, rayCount);
                var rayDirections = circle.Vertices;
                var totalRadius = input.MaxRadius + minRadius;
                maxTotalScore += Math.PI * totalRadius * totalRadius;

                var heightTransform = new Transform(vertexAverage.X, vertexAverage.Y, height);
                circle = heightTransform.OfPolygon(circle);

                var maxCircle = new Circle(heightTransform.Origin, totalRadius);


                var filteredFaces = filterFaces(allFaces.SelectMany(f => f.Values).ToList(), maxCircle);

                var rays = rayDirections.Select(i => new Ray(maxCircle.Center, i));
                List<Vector3> finalIsovistPoints = new List<Vector3>();

                foreach (var ray in rays)
                {
                    List<Vector3> allResults = new List<Vector3>();

                    foreach (var face in filteredFaces)
                    {
                        if (Intersects(ray, face, out Vector3 result))
                        {
                            allResults.Add(result);
                        }
                    }
                    if (allResults.Count > 0)
                    {
                        var resultsOrdered = allResults.OrderBy(r => r.DistanceTo(ray.Origin));
                        finalIsovistPoints.Add(resultsOrdered.First());
                    }
                    else
                    {
                        finalIsovistPoints.Add(ray.Origin + ray.Direction.Unitized() * totalRadius);
                    }
                }

                var isovist = new Polygon(finalIsovistPoints);
                totalScore += isovist.Area();

                Mesh mesh = CreateMeshFromRays(finalIsovistPoints, maxCircle, rayCount);
                var isovistElement = new Isovist(mesh);

                model.AddElement(isovistElement);
            
            }
            var outputs = new ViewRadiusOutputs((totalScore / maxTotalScore) * 100);
            outputs.model = model;
            return outputs;


        }

        private static bool IsInRoughDirection(Face face, Ray ray, Circle maxCircle)
        {
            var faceCenter = face.Outer.ToPolygon().Centroid();
            var dirToFace = (faceCenter - maxCircle.Center).Unitized();
            return ray.Direction.Unitized().Dot(dirToFace) > 0.8;
        }

        private static Mesh CreateMeshFromRays(List<Vector3> finalIsovistPoints, Circle maxCircle, int rayCount)
        {
            var badColor = new Color(0, 0, 1.0, 0.8);
            var goodColor = new Color(0, 1.0, 0.8, 0.8);
            var meshOut = new Mesh();
            var cwXform = new Transform();
            cwXform.Move(-1 * maxCircle.Center);
            cwXform.Rotate(Vector3.ZAxis, 360.0 / (rayCount * 2));
            cwXform.Move(maxCircle.Center);

            var ccwXForm = new Transform();
            ccwXForm.Move(-1 * maxCircle.Center);
            ccwXForm.Rotate(Vector3.ZAxis, 360.0 / (rayCount * -2));
            ccwXForm.Move(maxCircle.Center);

            for (int i = 0; i < rayCount; i++)
            {
                var pt = finalIsovistPoints[i];
                var distNormalized = pt.DistanceTo(maxCircle.Center) / maxCircle.Radius;
                distNormalized = Math.Min(distNormalized, 0.999);
                var color = badColor.Lerp(goodColor, distNormalized);
                var A = new Elements.Geometry.Vertex(maxCircle.Center, Vector3.ZAxis, color);
                var B = new Elements.Geometry.Vertex(ccwXForm.OfPoint(pt), Vector3.ZAxis, color);
                var C = new Elements.Geometry.Vertex(cwXform.OfPoint(pt), Vector3.ZAxis, color);
                meshOut.AddVertex(A);
                meshOut.AddVertex(B);
                meshOut.AddVertex(C);
                meshOut.AddTriangle(A, B, C);
            }

            return meshOut;
        }

        private static List<Elements.Geometry.Solids.Face> filterFaces(IEnumerable<Elements.Geometry.Solids.Face> faces, Circle maxCircle)
        {
            var filtered = new List<Elements.Geometry.Solids.Face>();
            var elevation = maxCircle.Center.Z;
            var xyOrigin = new Vector3(maxCircle.Center.X, maxCircle.Center.Y);
            foreach (var face in faces)
            {
                var poly = face.Outer.ToPolygon();
                var normal = poly.Normal();
                var centroid = poly.Centroid();

                var rayvec = centroid - maxCircle.Center;
                //check normal
                var rayvecU = rayvec.Unitized();
                if (rayvecU.Dot(normal) >= 0) continue;

                //check distance
                var rayVecProjected = new Vector3(rayvec.X, rayvec.Y);
                if (rayVecProjected.Length() > maxCircle.Radius) continue;

                //check elevation
                var bbox = new BBox3(poly.Vertices);
                if (bbox.Min.Z > elevation || bbox.Max.Z < elevation) continue;


                filtered.Add(face);
            }
            return filtered;
        }


        internal static bool Intersects(Ray ray, solids.Face face, out Vector3 result)
        {
            var edges = face.Outer.Edges;
            var a = edges[0].Vertex.Point;
            var b = edges[1].Vertex.Point;
            var c = edges[2].Vertex.Point;
            var plane = new Plane(a, b, c);

            if (ray.Intersects(plane, out Vector3 intersection))
            {
                var boundaryPolygon = face.Outer.ToPolygon();
                var transformToPolygon = new Transform(plane.Origin, plane.Normal);
                var transformFromPolygon = new Transform(transformToPolygon);
                transformFromPolygon.Invert();
                var transformedPolygon = transformFromPolygon.OfPolygon(boundaryPolygon);
                var transformedIntersection = transformFromPolygon.OfVector(intersection);
                if (transformedPolygon.Contains(transformedIntersection) || transformedPolygon.Touches(transformedIntersection))
                {
                    result = intersection;
                    return true;
                }
            }
            result = default(Vector3);
            return false;
        }
        private static List<Elements.Geometry.Solids.SolidOperation> filterSolids(IEnumerable<Mass> masses, Circle maxCircle)
        {
            var filtered = new List<Elements.Geometry.Solids.SolidOperation>();
            var elevation = maxCircle.Center.Z;
            var xyOrigin = new Vector3(maxCircle.Center.X, maxCircle.Center.Y);
            foreach (var mass in masses)
            {
                var xform = mass.Transform;
                var height = mass.Height;
                if (!(elevation >= xform.Origin.Z && elevation <= xform.Origin.Z + height)) continue;

                var massCenter = mass.ProfileTransformed().Perimeter.Vertices.Average();
                massCenter = new Vector3(massCenter.X, massCenter.Y);
                if (xyOrigin.DistanceTo(massCenter) > maxCircle.Radius * 1.1) continue;

                filtered.AddRange(mass.Representation.SolidOperations.Select(s => s));

            }
            return filtered;
        }
    }
}