using System;

namespace ActivatorTest
{
    class Program
    {
        abstract class Base
        {
            public Base()
            {
                Console.WriteLine($"{this.GetType().Name} is instatiated");
            }
            public Base(object o) { }
        }

        class TypeOfClass : Base { public TypeOfClass() : base() { } }
        class GenericArgumentClass : Base { public GenericArgumentClass() : base() { } }
        class GetTypeClass : Base
        {
            public GetTypeClass() : base() { }
            public GetTypeClass(object o) : base(o) { }
        }

        static void Main(string[] args)
        {
            TryConstruct(typeof(TypeOfClass));
            TryConstruct(new GetTypeClass(null).GetType());
            TryConstruct<GenericArgumentClass>();
            Console.ReadKey();
        }

        static void TryConstruct(Type type)
        {
            try
            {
                Activator.CreateInstance(type);
            } catch(Exception e)
            {
                Console.WriteLine($"Could not instatiate {type.Name} class");
                Console.WriteLine(e.Message);
            }
        }
        static void TryConstruct<T>()
        {
            try
            {
                Activator.CreateInstance<T>();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not instatiate {typeof(T).Name} class");
                Console.WriteLine(e.Message);
            }
        }
    }
}
